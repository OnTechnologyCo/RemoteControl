// WebSocket 을 직접 구현한다.
//
// .NET Framework 4 에서 쓸 수 있는 내장 WebSocket 서버는 HttpListener 에 묶여 있고
// 윈도우 버전을 탄다. 옆 프로젝트(FileSharer)가 이미 TcpListener 로 HTTP 를 직접
// 처리하고 있어서, 같은 자리에 프레이밍만 얹는 편이 의존성도 없고 예측 가능하다.
// 핸드셰이크와 프레이밍은 합쳐서 200줄이 안 된다.
using System;
using System.IO;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace RemoteControl
{
    internal enum WsOp { Continuation = 0, Text = 1, Binary = 2, Close = 8, Ping = 9, Pong = 10 }

    internal sealed class WsMessage
    {
        public WsOp Op;
        public byte[] Data;
        public string Text { get { return Encoding.UTF8.GetString(Data); } }
    }

    internal sealed class WebSocketConn : IDisposable
    {
        private const string Magic = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";

        private readonly Stream stream;
        private readonly object sendGate = new object();
        private volatile bool closed;

        internal WebSocketConn(Stream s) { stream = s; }

        internal bool Closed { get { return closed; } }

        /// <summary>
        /// 요청 헤더의 Sec-WebSocket-Key 로 악수 응답을 만든다.
        /// 규격이 정한 고정 문자열을 붙여 SHA1 을 낸 뒤 base64 로 돌려주게 되어 있다.
        /// </summary>
        internal static string BuildHandshake(string key)
        {
            byte[] hash;
            using (SHA1 sha = SHA1.Create())
                hash = sha.ComputeHash(Encoding.ASCII.GetBytes(key.Trim() + Magic));

            return "HTTP/1.1 101 Switching Protocols\r\n" +
                   "Upgrade: websocket\r\n" +
                   "Connection: Upgrade\r\n" +
                   "Sec-WebSocket-Accept: " + Convert.ToBase64String(hash) + "\r\n\r\n";
        }

        // ---------------------------------------------------------------- 받기

        /// <summary>
        /// 한 덩어리(메시지)를 읽는다. 조각나서 온 것은 합쳐서 돌려준다.
        /// 연결이 끝났으면 null.
        /// </summary>
        internal WsMessage Receive()
        {
            WsOp msgOp = WsOp.Continuation;
            MemoryStream buf = null;

            while (true)
            {
                byte[] head = ReadExact(2);
                if (head == null) return null;

                bool fin = (head[0] & 0x80) != 0;
                WsOp op = (WsOp)(head[0] & 0x0F);
                bool masked = (head[1] & 0x80) != 0;
                long len = head[1] & 0x7F;

                if (len == 126)
                {
                    byte[] e = ReadExact(2);
                    if (e == null) return null;
                    len = (e[0] << 8) | e[1];
                }
                else if (len == 127)
                {
                    byte[] e = ReadExact(8);
                    if (e == null) return null;
                    len = 0;
                    for (int i = 0; i < 8; i++) len = (len << 8) | e[i];
                }

                // 브라우저가 보내는 프레임은 반드시 마스킹되어 있다.
                // 조작 명령은 작으므로 상한을 넉넉히 두되 무한정 받지는 않는다.
                if (len < 0 || len > 8 * 1024 * 1024) return null;

                byte[] mask = null;
                if (masked)
                {
                    mask = ReadExact(4);
                    if (mask == null) return null;
                }

                byte[] payload = len == 0 ? new byte[0] : ReadExact((int)len);
                if (payload == null) return null;

                if (mask != null)
                    for (int i = 0; i < payload.Length; i++) payload[i] ^= mask[i & 3];

                switch (op)
                {
                    case WsOp.Close:
                        closed = true;
                        return null;

                    case WsOp.Ping:
                        Send(WsOp.Pong, payload);
                        continue;

                    case WsOp.Pong:
                        continue;
                }

                if (op != WsOp.Continuation) msgOp = op;

                if (fin && buf == null)
                    return new WsMessage { Op = msgOp, Data = payload };

                if (buf == null) buf = new MemoryStream();
                buf.Write(payload, 0, payload.Length);

                if (fin)
                    return new WsMessage { Op = msgOp, Data = buf.ToArray() };
            }
        }

        private byte[] ReadExact(int n)
        {
            byte[] b = new byte[n];
            int got = 0;
            while (got < n)
            {
                int r;
                try { r = stream.Read(b, got, n - got); }
                catch { return null; }

                if (r <= 0) return null;
                got += r;
            }
            return b;
        }

        // ---------------------------------------------------------------- 보내기

        internal void SendText(string s)
        {
            Send(WsOp.Text, Encoding.UTF8.GetBytes(s));
        }

        internal void SendBinary(byte[] data)
        {
            Send(WsOp.Binary, data);
        }

        internal void Send(WsOp op, byte[] payload)
        {
            if (closed) return;
            if (payload == null) payload = new byte[0];

            // 서버가 보내는 프레임은 마스킹하지 않는다(규격).
            byte[] head;
            int n = payload.Length;

            if (n < 126)
            {
                head = new byte[2];
                head[1] = (byte)n;
            }
            else if (n <= 0xFFFF)
            {
                head = new byte[4];
                head[1] = 126;
                head[2] = (byte)(n >> 8);
                head[3] = (byte)n;
            }
            else
            {
                // 길이를 64비트로 적는다.
                //
                // 여기서 n 을 int 그대로 밀면 안 된다. C# 은 int 시프트에서 자릿수를
                // 32로 나눈 나머지만 쓰기 때문에 n >> 32 가 n >> 0 이 되어 버린다.
                // 그러면 길이 필드가 엉뚱한 값이 되고, 브라우저는 프로토콜 오류로
                // 연결을 끊는다. 작은 프레임은 이 경로를 안 타서 멀쩡해 보였다.
                long ln = n;
                head = new byte[10];
                head[1] = 127;
                for (int i = 0; i < 8; i++) head[9 - i] = (byte)(ln >> (8 * i));
            }
            head[0] = (byte)(0x80 | (int)op);

            lock (sendGate)
            {
                try
                {
                    stream.Write(head, 0, head.Length);
                    if (n > 0) stream.Write(payload, 0, n);
                    stream.Flush();
                }
                catch { closed = true; }
            }
        }

        internal void Close()
        {
            if (closed) return;
            try { Send(WsOp.Close, new byte[0]); }
            catch { }
            closed = true;
        }

        internal void Abort()
        {
            // 느린 전송의 sendGate를 기다리면 입력 해제와 수신 스레드까지 멈춘다.
            closed = true;
            try { stream.Close(); } catch { }
        }

        public void Dispose() { Close(); }
    }
}
