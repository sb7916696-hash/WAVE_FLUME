The Microsoft Visual C++ runtime installer is bundled into the Wave Flume setup program, but it is not stored in this
repository (it is a 24 MB Microsoft download). Before building the installer, download it into this folder:

  https://aka.ms/vs/17/release/vc_redist.x64.exe   ->   installer\prereq\vc_redist.x64.exe

then run:  powershell -File installer\build-release.ps1
