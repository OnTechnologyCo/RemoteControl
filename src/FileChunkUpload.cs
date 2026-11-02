// Browser file upload: small authenticated multipart chunks with offset verification.
// Cloudflare 100 MB single-request limits and network interruptions no longer
// require the entire file to be retransmitted. A transfer belongs to one login.
using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace RemoteControl
{
    internal static class FileChunkUpload
    {
        internal const int ChunkBytes = 2 * 1024 * 1024;
        internal const long MaxFileBytes = 16L * 1024 * 1024 * 1024;
        private const int MaxRequests = ChunkBytes + 64 * 1024;
        private const int MaxActive = 8;
        private static readonly TimeSpan Lifetime = TimeSpan.FromHours(24);
        private static readonly object Global = new object();
        private static readonly Dictionary<string, Transfer> Transfers =
            new Dictionary<string, Transfer>(StringComparer.OrdinalIgnoreCase);

        // Tests set a standalone private directory, never an operational PC path.
        internal static string TestStageRoot = null;

        private sealed class Transfer
        {
            internal readonly object Gate = new object();
            internal string Id, Owner, Folder, Name, Stage, Partial, FileName;
            internal long Size, Offset;
            internal string State = "uploading";
            internal string Sha256 = "";
            internal DateTime Updated;
        }

        private static string StageRoot
        {
            get
            {
                return TestStageRoot ?? Path.Combine(SvcLog.Dir, "browser-uploads");
            }
        }

        private static string Escape(string text) { return Json.Escape(text ?? ""); }
        private static string Error(string message, string code)
        {
            return "{\"ok\":false,\"err\":\"" + Escape(message) + "\",\"code\":\"" + Escape(code) + "\"}";
        }
        private static string View(Transfer t)
        {
            return "{\"ok\":true,\"id\":\"" + t.Id + "\",\"offset\":" + t.Offset +
                   ",\"size\":" + t.Size + ",\"done\":" + (t.State == "completed" ? "true" : "false") +
                   ",\"sha256\":\"" + t.Sha256 + "\",\"file\":\"" + Escape(t.FileName ?? "") + "\"}";
        }
        private static bool ValidId(string id)
        {
            Guid result;
            return !string.IsNullOrEmpty(id) && id.Length == 32 &&
                Guid.TryParseExact(id, "N", out result);
        }
        private static bool Same(string a, string b)
        {
            return string.Equals(a, b, StringComparison.Ordinal);
        }
        private static string Hash(string file)
        {
            using (FileStream stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 256 * 1024))
            using (SHA256 sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
        }

        private static void DeleteStage(Transfer t)
        {
            try { Directory.Delete(t.Stage, true); } catch { }
        }
        private static void Collect()
        {
            // Called inside Global only. No user directories are searched or removed.
            DateTime now = DateTime.UtcNow;
            List<string> old = new List<string>();
            foreach (KeyValuePair<string, Transfer> item in Transfers)
            {
                Transfer t = item.Value;
                if (now - t.Updated < Lifetime) continue;
                lock (t.Gate) { if (now - t.Updated >= Lifetime) { DeleteStage(t); old.Add(item.Key); } }
            }
            foreach (string id in old) Transfers.Remove(id);

            // Stage orphaned after an unexpected process shutdown: remove only known
            // GUID-named directories older than 24 hours under our protected root.
            try
            {
                if (!Directory.Exists(StageRoot)) return;
                foreach (string dir in Directory.GetDirectories(StageRoot))
                {
                    string id = Path.GetFileName(dir);
                    if (!ValidId(id) || Transfers.ContainsKey(id)) continue;
                    if (DateTime.UtcNow - Directory.GetLastWriteTimeUtc(dir) > Lifetime)
                        try { Directory.Delete(dir, true); } catch { }
                }
            }
            catch { }
        }

        private static Transfer Get(string owner, string id)
        {
            if (!ValidId(id)) return null;
            lock (Global)
            {
                Transfer t;
                if (!Transfers.TryGetValue(id, out t) || !Same(owner, t.Owner)) return null;
                return t;
            }
        }

        internal static string Begin(string owner, string folder, string relative, string name,
                                     long size, string resume)
        {
            if (string.IsNullOrEmpty(owner)) return Error("인증 세션이 없습니다.", "auth");
            if (size < 0 || size > MaxFileBytes)
                return Error("파일 크기는 최대 16GiB까지 지원합니다.", "size");
            if (name == null || name.Length > 512)
                return Error("파일 이름이 올바르지 않습니다.", "name");
            string safe = Files.SafeName(name);
            if (safe.Length == 0) return Error("파일 이름이 올바르지 않습니다.", "name");
            if (string.IsNullOrWhiteSpace(folder)) return Error("대상 폴더를 선택해 주세요.", "path");
            string destination;
            try
            {
                if (!Directory.Exists(Path.GetFullPath(folder)))
                    return Error("대상 폴더가 없습니다.", "path");
                if (string.IsNullOrEmpty(relative))
                {
                    destination = Path.GetFullPath(folder);
                    if ((File.GetAttributes(destination) & FileAttributes.ReparsePoint) != 0)
                        return Error("연결된 폴더에는 업로드할 수 없습니다.", "path");
                }
                else
                {
                    string reason = Files.UploadDirectory(folder, relative, out destination);
                    if (!string.IsNullOrEmpty(reason)) return Error(reason, "path");
                }
            }
            catch (Exception ex) { return Error(ex.Message, "path"); }

            lock (Global)
            {
                Collect();
                if (ValidId(resume))
                {
                    Transfer existing;
                    if (Transfers.TryGetValue(resume, out existing))
                    {
                        lock (existing.Gate)
                        {
                            if (!Same(existing.Owner, owner)) return Error("다른 인증 세션의 업로드입니다.", "auth");
                            if (!Same(existing.Folder, destination) || !Same(existing.Name, safe) ||
                                existing.Size != size)
                                return Error("이어받기 파일 정보가 일치하지 않습니다.", "mismatch");
                            existing.Updated = DateTime.UtcNow;
                            return View(existing);
                        }
                    }
                    // Lost server state: fresh transfer with offset 0, never silently
                    // attach another user's partial file.
                }
                int uploading = 0;
                foreach (Transfer value in Transfers.Values)
                    if (value.State == "uploading") uploading++;
                if (uploading >= MaxActive) return Error("업로드가 8개 진행 중입니다.", "busy");

                Transfer t = new Transfer();
                t.Id = Guid.NewGuid().ToString("N");
                t.Owner = owner;
                t.Folder = destination;
                t.Name = safe;
                t.Size = size;
                t.Stage = Path.Combine(StageRoot, t.Id);
                t.Partial = Path.Combine(t.Stage, "content.part");
                t.Updated = DateTime.UtcNow;
                try
                {
                    Directory.CreateDirectory(t.Stage);
                    using (FileStream file = new FileStream(t.Partial, FileMode.CreateNew, FileAccess.Write,
                                                            FileShare.None)) { }
                    Transfers.Add(t.Id, t);
                    return View(t);
                }
                catch (Exception ex)
                {
                    DeleteStage(t);
                    return Error("업로드 임시 저장소를 만들지 못했습니다: " + ex.Message, "storage");
                }
            }
        }

        internal static string Status(string owner, string id)
        {
            Transfer t = Get(owner, id);
            if (t == null) return Error("이어받기 세션이 없거나 만료되었습니다.", "missing");
            lock (t.Gate)
            {
                t.Updated = DateTime.UtcNow;
                return View(t);
            }
        }

        // Multipart chunks preserve browser/DLP compatibility. The existing
        // streaming Files.ReceiveMultipart parser writes a small temporary file;
        // only a complete, size-verified chunk is appended to the partial file.
        internal static string Chunk(string owner, string id, long offset,
                                     Stream stream, byte[] preRead, long length, string boundary, string expectedHash)
        {
            Transfer t = Get(owner, id);
            if (t == null) return Error("업로드 세션이 없거나 만료되었습니다.", "missing");
            lock (t.Gate)
            {
                if (t.State != "uploading") return Error("이미 완료된 업로드입니다.", "completed");
                if (offset != t.Offset) return Error("전송 위치가 바뀌었습니다. 상태를 다시 확인하세요.", "offset");
                if (offset < 0 || offset >= t.Size) return Error("전송 위치가 올바르지 않습니다.", "offset");
                long expectedBytes = Math.Min(ChunkBytes, t.Size - offset);
                if (length < expectedBytes || length > Math.Min(MaxRequests, expectedBytes + 65536))
                    return Error("분할 요청의 크기가 잘못됐습니다.", "length");
                if (string.IsNullOrEmpty(boundary) || boundary.Length > 180 ||
                    boundary.IndexOfAny(new[] { '\r', '\n' }) >= 0)
                    return Error("multipart boundary가 올바르지 않습니다.", "boundary");
                if (string.IsNullOrEmpty(expectedHash) || expectedHash.Length != 64 ||
                    !System.Text.RegularExpressions.Regex.IsMatch(expectedHash, "\\A[a-fA-F0-9]{64}\\z"))
                    return Error("분할 SHA-256 값이 필요합니다.", "checksum");

                string chunk = Path.Combine(t.Stage, "segment");
                try
                {
                    if (File.Exists(chunk)) File.Delete(chunk);
                    string why = Files.ReceiveMultipart(stream, preRead, length, t.Stage, "segment", boundary);
                    if (!string.IsNullOrEmpty(why)) return Error(why, "incomplete");
                    if (!File.Exists(chunk) || new FileInfo(chunk).Length != expectedBytes)
                        return Error("전송받은 조각의 실제 크기가 일치하지 않습니다.", "size");
                    string actualHash = Hash(chunk);
                    if (!Same(actualHash, expectedHash.ToLowerInvariant()))
                        return Error("전송 조각의 SHA-256이 일치하지 않습니다.", "checksum");

                    using (FileStream target = new FileStream(t.Partial, FileMode.Open, FileAccess.Write, FileShare.None))
                    {
                        if (target.Length != offset) return Error("임시 파일 크기가 달라졌습니다.", "state");
                        using (FileStream source = new FileStream(chunk, FileMode.Open, FileAccess.Read, FileShare.Read))
                        {
                            target.Position = offset;
                            source.CopyTo(target, 128 * 1024);
                        }
                        target.Flush(true);
                    }
                    t.Offset += expectedBytes; // only committed after flush
                    t.Updated = DateTime.UtcNow;
                    return View(t);
                }
                catch (Exception ex)
                {
                    // If an append failed partway, restore the previous committed boundary.
                    try
                    {
                        using (FileStream f = new FileStream(t.Partial, FileMode.Open, FileAccess.Write, FileShare.None))
                            if (f.Length > t.Offset) f.SetLength(t.Offset);
                    }
                    catch { }
                    return Error("조각 저장 실패: " + ex.Message, "io");
                }
                finally { try { File.Delete(chunk); } catch { } }
            }
        }

        internal static string Finish(string owner, string id)
        {
            Transfer t = Get(owner, id);
            if (t == null) return Error("업로드 세션이 없거나 만료되었습니다.", "missing");
            lock (t.Gate)
            {
                if (t.State == "completed") return View(t);
                if (t.Offset != t.Size || !File.Exists(t.Partial) || new FileInfo(t.Partial).Length != t.Size)
                    return Error("업로드가 아직 완료되지 않았습니다.", "incomplete");

                // Never overwrite a pre-existing file; preserve Files.ReceiveMultipart naming.
                string temporary = null;
                try
                {
                    string target = Files.Unique(Path.Combine(t.Folder, t.Name));
                    temporary = Path.Combine(t.Folder, ".rc-upload-" + t.Id + ".tmp");
                    using (FileStream from = new FileStream(t.Partial, FileMode.Open, FileAccess.Read, FileShare.Read))
                    using (FileStream to = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        from.CopyTo(to, 256 * 1024);
                        to.Flush(true);
                    }
                    if (new FileInfo(temporary).Length != t.Size)
                        return Error("최종 파일 복사 크기가 일치하지 않습니다.", "size");
                    t.Sha256 = Hash(t.Partial);
                    File.Move(temporary, target);
                    temporary = null;
                    t.FileName = Path.GetFileName(target);
                    t.State = "completed";
                    t.Updated = DateTime.UtcNow;
                    DeleteStage(t);
                    return View(t);
                }
                catch (Exception ex) { return Error("최종 저장 실패: " + ex.Message, "io"); }
                finally { if (temporary != null) try { File.Delete(temporary); } catch { } }
            }
        }

        internal static string Cancel(string owner, string id)
        {
            Transfer t = Get(owner, id);
            if (t == null) return Error("업로드 세션이 없거나 만료되었습니다.", "missing");
            lock (Global)
            {
                lock (t.Gate)
                {
                    if (t.State == "uploading") DeleteStage(t);
                    Transfers.Remove(t.Id);
                    return "{\"ok\":true}";
                }
            }
        }
    }
}
