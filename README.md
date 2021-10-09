# SystemUpgrade

개발 툴: Visual Studio 2019

개발 언어: C# WPF

목적:
     - Jetson TX2 system upgrade program를 위한 프로그램
     - 원격으로 파일을 전송(SFTP)하고 기존의 실행프로그램을 삭제, 복사한 후 재시작(SSH)
 
사용방법:
1. 대상과 ethernet 연결 (상단의 connect 버튼)
2. Load 버튼으로 설정파일을 불러옴
3. Upgrade를 눌러 설정파일의 내용에 따라 Upgrade (파일을 전송하고 명령을 실행)
4. Check를 누르면 설정파일의 내용에 따라 장비의 설정을 Check
옵션: Ctrl+G를 누르면 debug 창이 활성화되며 장비의 파일시스템을 확인하고 사용자가 원하는 SSH 명령을 실행할 수 있음
    
# Load 버튼을 누른 후 UI
![SystemUpgrade](https://user-images.githubusercontent.com/28644565/136670894-fc1ce0d5-cca2-474a-bef1-f45ea9a921b5.PNG)

# Upgrade를 실행한 후 UI + debug 창이 활성화된 모습
![SystemUpgrade2](https://user-images.githubusercontent.com/28644565/136670895-e7b400c6-7f22-48c6-a37c-0a8df3054ba4.PNG)
