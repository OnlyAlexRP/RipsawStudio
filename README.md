# Ripsaw Studio

Ripsaw Studio is a lightweight capture app for Windows, built to work with virtually any capture card, from generic AliExpress units to Elgato and AVerMedia devices, without hunting for drivers or manufacturer software. Plug your card in, see your gameplay, record it, with nothing sitting in between.

The picture reaches your screen the moment it arrives. Ripsaw Studio adds no buffering of its own: frames go from the card to the GPU to the glass, and the app is paced by your display rather than by a queue. VSync can be turned off or on to match your setup, which fixes the frame drops and freezing some cards show (the Razer Ripsaw HD among them), and colour range control switches between limited and full range so washed-out or crushed blacks match what your card is actually sending.

No wizard, no suite of tools you will never open. One job: getting your capture card's feed on screen and into a file, as directly as possible.

## What it does

- **Live preview** of any capture card Windows can see, at the card's own resolution and frame rate.
- **Recording** with full control: H.264 or HEVC, your choice of encoder (NVIDIA NVENC, Intel Quick Sync, AMD AMF or the CPU, on whichever GPU you want), constant bitrate, variable bitrate or constant quality. Recordings are written as fragmented MP4 by default, so the file stays playable even if the PC crashes mid take.
- **Instant Replay** keeps the last minutes of play in the background, ready to be saved as a clip the moment something worth keeping happens.
- **Screenshots**, saved as soon as you press the key.
- **Global shortcuts**: record, save a replay and take a screenshot from inside the game, without Ripsaw Studio being the focused window.
- **Captures library**: every recording, replay and screenshot in one grid, with filters, favourites, renaming, and deletion to the Recycle Bin.
- **Sound**: hear the game through your speakers or headset with adjustable latency, mix in a microphone with its own delay, or record the PC's own output instead so voice chat ends up in the clip.
- **Picture controls**: scaling, aspect ratio, brightness, contrast, saturation, colour range, and a smoothing pass for cheap cards that produce blocky pixels.
- **Profiles**: one per console, each with its own card, format, picture and sound settings.
- **Interface in English, French, Spanish, German and Russian**, following Windows automatically.

Streaming is not part of it, and is not meant to be. Ripsaw Studio works in OBS as a **Game Capture** source when you want to stream. Window Capture will not pick its picture up, so point OBS at the Ripsaw Studio window with Game Capture instead.

## Download

**1. Install Microsoft's .NET 8 Desktop Runtime**, once and for all. It is a free Microsoft component that many Windows apps share, and you may well have it already.

[Download it here](https://aka.ms/dotnet/8.0/windowsdesktop-runtime-win-x64.exe) (direct from Microsoft), or pick **.NET Desktop Runtime 8.0, x64** yourself on the [official page](https://dotnet.microsoft.com/download/dotnet/8.0). Run the installer, click through it, and that is the last time you will think about it.

**2. Download RipsawStudio_v1.6.zip** from the [Releases page](https://github.com/OnlyAlexRP/RipsawStudio/releases/latest).

Unzip it wherever you like, open the `RipsawStudio` folder and run `RipsawStudio.exe`. Ripsaw Studio itself installs nothing and writes nothing outside your user folder, so you can keep it on a USB drive if you want. Keep the folder intact: the app loads the files next to it.

The preview starts on its own when a card is found. If the window stays empty, open **Capture > Source** and pick your card and its format.

If double clicking the app does nothing, or Windows offers to search online for a missing component, step 1 is what is missing.

## If Windows warns you about the app

Ripsaw Studio is a small, freely distributed app that is not code signed, so Windows does not recognise it yet. Nothing is wrong with your download.

- **"Windows protected your PC" (SmartScreen)**: click **More info**, then **Run anyway**.
- **"Smart App Control has blocked a potentially dangerous app"**: Smart App Control offers no exception for a single app, so it has to be switched off to run unsigned software. Open the Start menu, search for **Windows Security**, go to **App & browser control**, then **Smart App Control settings**, and set it to **Off**. Bear in mind this is a system wide protection, so decide for yourself whether you want to turn it off.

If you would rather leave your security settings alone, Ripsaw Studio is also available on the [Microsoft Store](https://apps.microsoft.com/detail/9P9W4XR5XRPS), where the build is signed and verified, so it installs without any of this.

## Where your files go

- Recordings, replays and screenshots: in your Videos folder, under `Ripsaw Studio`, each in its own subfolder. All three are configurable.
- Settings, and a crash log if one is ever written: `%APPDATA%\RipsawStudio`.

Nothing else is touched, and nothing runs when the app is closed.

## A word on latency

Ripsaw Studio does not delay your picture, but it cannot remove the delay your capture card creates before the frame ever reaches the PC. That delay is part of the hardware and ranges from barely noticeable to well over a tenth of a second depending on the model. If the feed still feels late, **Settings > Measure** records every frame for ten seconds and writes a plain report telling you where the time actually goes.

For the lowest latency: leave VSync off, prefer an uncompressed format (NV12 or YUY2 rather than MJPG, which has to be decoded first), and keep the sound latency as low as your setup tolerates.

## Requirements

- Windows 10 or Windows 11, 64 bit.
- Microsoft .NET 8 Desktop Runtime, free, installed once (see Download above). The Microsoft Store build carries it, so it needs nothing.
- A capture card Windows already recognises, which covers practically every USB and PCIe card sold today.

## Links

- Releases: https://github.com/OnlyAlexRP/RipsawStudio/releases
- Website: https://onlyalexrp.itch.io/ripsaw-studio

## Development

Written by OnlyAlexRP, with the assistance of Claude AI for code restructuring and optimisation prior to publication.
