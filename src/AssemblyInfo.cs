// exe 의 [속성] → [자세히] 에 들어가는 값.
//
// 왜 이게 필요한가 — 오탐 때문이다.
//   이 값들이 비어 있으면 exe 속성 창의 [자세히] 탭이 통째로 빈칸이 된다.
//   Defender 의 클라우드 ML(`!ml` 이 붙는 판정)은 **회사·제품·설명·버전이 비었는지**
//   를 크게 본다. 정상 프로그램은 거의 다 채워 넣고, 급조된 것은 거의 다 비어 있기
//   때문이다. 여기에 "서명 없음 + 평판 0(처음 보는 파일)" 이 겹치면 하는 일과
//   상관없이 Trojan:Win32/Bearfoos.B!ml 같은 이름이 붙는다.
//
//   실제로 v1.4.0 까지 이 파일이 없어서 FileVersion 이 0.0.0.0, 회사·제품이 빈칸,
//   설명은 공백 한 칸이었다. 남에게 넘긴 설치 파일에서 오탐이 났다.
//
// 값을 지어내면 안 된다.
//   "Microsoft Corporation" 같은 것을 넣으면 오탐이 줄기는커녕 사칭으로 잡힌다.
//   실제로 만든 사람의 이름을 그대로 적는 것이 맞다.
//
// 판 번호는 MainForm.Version 하나만 고치면 여기까지 따라온다.
//   두 군데에 따로 적으면 언젠가 반드시 어긋난다 - 설치 관리자(Setup.cs)가 판 번호를
//   따로 들고 있다가 본체는 1.4.0 인데 설치 파일은 1.2.3 으로 나가 있었다.
using System.Reflection;
using System.Runtime.InteropServices;

// 속성 창의 "파일 설명". 작업 관리자에도 이 이름으로 보인다.
[assembly: AssemblyTitle("원격제어")]
[assembly: AssemblyDescription("웹브라우저로 이 PC 를 보고 조작한다")]
[assembly: AssemblyProduct("원격제어")]
[assembly: AssemblyCompany("ontech")]
[assembly: AssemblyCopyright("Copyright (C) 2026 ontech")]

// AssemblyVersion 을 판마다 올리면 이 어셈블리를 참조하는 쪽이 깨진다 - 여기서는
// 참조하는 것이 없지만, 습관대로 고정해 두고 실제 판 번호는 FileVersion 에 싣는다.
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion(RemoteControl.MainForm.Version + ".0")]
[assembly: AssemblyInformationalVersion(RemoteControl.MainForm.Version)]

// COM 에 노출할 것이 없다. 켜 두면 다른 프로그램이 이 어셈블리의 형식을 만들 수 있다.
[assembly: ComVisible(false)]
