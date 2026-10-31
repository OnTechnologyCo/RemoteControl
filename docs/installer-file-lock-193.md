# v1.9.3 설치 중 EXE 파일 잠금 수정

2026-10-31

## 증상과 원인

기존 설치를 업데이트할 때 RemoteControl.exe가 다른 프로세스에서 사용 중이라는 오류로 설치가 중단될 수 있었습니다. Inno Setup의 런타임 오류는 설치 엔진이 반환한 파일 공유 오류를 표시한 것입니다.

텔레그램 입력을 비워 두고 기존 암호화 자격 증명 파일이 없는 경우, TelegramCredentials.PreserveFrom이 이전 본체의 상수 메타데이터를 ReflectionOnlyLoadFrom으로 읽었습니다. 이 API는 설치 엔진 프로세스가 종료될 때까지 이전 EXE를 매핑하므로, 같은 프로세스의 후속 Sys.Extract가 파일을 덮어쓰지 못했습니다. 이전 본체에 봇 토큰 상수가 없어도 발생합니다. 설치 경로와 관계없이 재현됩니다.

## 수정

이전 EXE를 File.ReadAllBytes로 읽은 뒤 ReflectionOnlyLoad(byte[])로 메모리에서 메타데이터를 확인합니다. 파일 핸들은 읽기 직후 닫히고 이미지 배열은 사용 후 지웁니다. 이전 본체를 실행하거나 정적 생성자·속성 getter를 호출하지 않습니다.

기존 암호화 토큰은 검증만 하고 그대로 보존합니다. 구버전 상수 토큰 이전, 관리자/소유자/ACL 검증과 잘못된 토큰 거부 동작을 유지합니다. 설치 중지/시작 및 경로 선택 동작은 변경하지 않았습니다.

## 검증

- 수정 전 동일 테스트에서 NoToken.exe 교체 시 IOException(다른 프로세스에서 사용 중)을 재현했습니다.
- bench/Test-InstallerUpgrade.ps1: 토큰 없음/빈 토큰/상수 토큰/속성/잘못된 상수/기존 암호화 자격 증명/신규 설치를 격리 파일로 검사합니다. 메타데이터 확인 직후 같은 프로세스에서 덮어쓰기와 삭제가 가능해야 합니다.
- -BuiltSetup은 실제 배포 엔진의 PreserveFrom과 Sys.Extract를 연속 호출하고 설치된 EXE와 내장 본체의 SHA-256을 비교합니다.
- bench/Test-TelegramSetup.ps1, bench/Test-SecurityPolicy.ps1 및 배포 패키지 검사를 함께 실행합니다.
- 운영 중인 원격제어 서비스와 실제 설정을 변경하지 않는 격리 검사입니다. 사용자 PC의 전체 설치 마법사 완료 여부는 별도로 확인해야 합니다.

## 업데이트

실패한 구버전 설치 창을 닫고 v1.9.3 RemoteControl_Setup.exe로 다시 설치합니다. 기존 설정과 설치 위치를 유지하며, 토큰을 새로 입력할 필요는 없습니다. 원격제어 서비스가 교체되는 동안 해당 원격 연결은 잠시 끊길 수 있습니다.

## English

Updates could fail with a sharing violation because legacy Telegram metadata migration loaded the previous executable with ReflectionOnlyLoadFrom and kept it mapped inside the installer process. Metadata is now loaded from an in-memory byte array so extraction can immediately replace the old executable. Existing encrypted credentials and security checks are preserved. Isolated regression tests cover migration and replacement using both source and the packaged installer engine.
