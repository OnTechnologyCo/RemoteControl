// 브라우저에 보낼 화면 크기 목록이 제대로 나오는지 확인한다.
// DEVMODE 구조체는 필드 하나만 어긋나도 조용히 빈 목록이 나온다.
using System;
using System.Drawing;
using RemoteControl;

class ResBench
{
    static void Main()
    {
        Console.WriteLine("현재 크기: {0}", Display.Current());

        Console.WriteLine("\n이 PC 가 받아 주는 크기 (전체)");
        foreach (Size s in Display.Modes()) Console.WriteLine("   {0} x {1}", s.Width, s.Height);

        Console.WriteLine("\n브라우저에 보낼 목록 (FHD 이하)");
        foreach (Size s in Display.Choices()) Console.WriteLine("   {0} x {1}", s.Width, s.Height);

        Console.WriteLine("\n1920x1080 지원: {0}", Display.Supports(1920, 1080));
        Console.WriteLine("\nEnter 를 누르면 닫힙니다");
        Console.ReadLine();
    }
}
