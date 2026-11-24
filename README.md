# StarSavior 의상 편집 뷰어

스타세이비어 Steam 클라이언트의 캐릭터와 의상을 불러오는 Windows용 편집 뷰어입니다.

**현재 버전: 1.14.0** · Windows 10 이상 · Steam 게임 설치 필요

[최신 공개 버전 다운로드](https://github.com/KoreaBigAnt/StarSavior-CostumeViewer/releases/latest)

압축을 풀고 `StarSavior-CostumeViewer.exe`를 실행한 뒤 자동으로 찾은 Steam 게임 폴더를 확인하세요. 게임 리소스는 설치 폴더에서 읽으며 프로그램에 포함되지 않습니다.

- 캐릭터·의상·손상의상, 스킬과 의상파괴 연출 보기
- UV 부위 숨김, Ctrl 부분 지우기, PNG 내보내기
- 체형과 물리 조절, 키 조절, 살찜 최대 150%
- 프로필 이력 저장·불러오기, Blender 메시 편집, 게임용 ZIP 내보내기

키와 살찜의 무릎 변형은 무작위 10개 캐릭터에서 기본·최소 키·최대 키·최대 살찜 및 조합 60가지의 정면·측면과 실제 메시를 확인했습니다. 다른 의상·애니메이션 조합은 결과가 다를 수 있습니다.

소스 빌드: .NET SDK 설치 후 PowerShell에서 `./Build-Public.ps1 -OutputDirectory ./build`를 실행하세요. 기본 Steam 설치 위치가 다르면 `-GameRoot`를 지정합니다. 게임 업데이트로 실행 파일 구조가 바뀌면 호환성 업데이트가 필요할 수 있습니다.

게임에서 내보낸 모드는 [StarSavior ModManager](https://github.com/KoreaBigAnt/StarSavior-ModManager) 1.4.0 이상을 사용하세요.
