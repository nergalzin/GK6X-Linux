# GK6X Linux

Native Linux app for **GK6X keyboards** (GK61, GK64, GK84, SK61 and other models from the official software) with
**realtime RGB lighting**, key remapping, macros and lighting effects, controlled from the command line or from a local
control page with a live keyboard preview.

- Runs natively on Linux with .NET 8 or newer: no mono, no Windows software, no `sudo` while running.
- Lighting changes are instant. The control page shows exactly what the keyboard is receiving.
- Key mapping, macros and the original GK6X commands still work.
- Tested on a **SK61S RGB**. Other GK6X models should work. If yours doesn't, open an issue.

Maintained by [Nergalzin](https://github.com/nergalzin). Based on [pixeltris/GK6X](https://github.com/pixeltris/GK6X)
(MIT, archived upstream); see [Credits and license](#credits-and-license).

![GK6X lighting control page](docs/screenshot.png)

*Control page (`gk6x ui`) on a SK61S RGB: the keys show the colors the keyboard is receiving in real time; click or drag to paint keys.*

## Contents

- [Install](#install)
- [Use](#use)
- [Control page](#control-page)
- [Update](#update)
- [Uninstall](#uninstall)
- [Troubleshooting](#troubleshooting)
- [Credits and license](#credits-and-license)

## Install

1. **Install the .NET SDK (8 or newer) and git:**

   | Distro | Command |
   |---|---|
   | Arch / CachyOS | `sudo pacman -S dotnet-sdk git` |
   | Fedora | `sudo dnf install dotnet-sdk-8.0 git` |
   | Ubuntu / Debian | `sudo apt install dotnet-sdk-8.0 git` |

2. **Download the code:**
   ```sh
   git clone https://github.com/nergalzin/GK6X-Linux.git
   cd GK6X-Linux
   ```

3. **Run the installer as your normal user** (not with `sudo`):
   ```sh
   ./linux/install.sh --autostart
   ```
   - `--autostart` starts the lighting service at every login. Leave it out if you only want to start it by hand.
   - The installer asks for your password **once**, and only to add the udev rule. That rule lets your user talk to this
     keyboard without root, and it only touches this keyboard's `hidraw` devices.
   - The installer also adds a **"GK6X Lighting"** shortcut to your app menu.

4. **Put `~/.local/bin` in your `PATH`** if the installer warned about it:
   - fish: `fish_add_path ~/.local/bin`
   - bash: add `export PATH="$HOME/.local/bin:$PATH"` to `~/.bashrc`

5. **Replug the keyboard once**, then check it:
   ```sh
   gk6x list      # every line should say open=yes
   systemctl --user start gk6x
   gk6x status
   ```

Where everything goes:

| Path | What |
|---|---|
| `~/.local/share/gk6x/app` | the program |
| `~/.local/bin/gk6x` | the `gk6x` command |
| `~/.config/gk6x/` | your settings, key mappings, lighting files and macros (never touched by updates) |
| `~/.config/systemd/user/gk6x.service` | lighting service |
| `~/.local/share/applications/gk6x.desktop` | app menu shortcut |
| `/etc/udev/rules.d/70-gk6x.rules` | keyboard access rule (the only file that needs `sudo`) |

## Use

The lighting service (`gk6x daemon`, or `systemctl --user start gk6x`) must be running for the commands below.

```sh
gk6x color ff0000                  # solid color
gk6x effect rainbow --speed 60     # see all effects with: gk6x effects
gk6x brightness 70                 # 0 - 100
gk6x speed 40                      # 1 - 100
gk6x key W 00ff00                  # paint one key ('off' removes it)
gk6x clear                         # remove all painted keys
gk6x off                           # all lights off
gk6x status
gk6x ui                            # control page in its own window (--tab = browser tab)
gk6x stop                          # stop the service; the keyboard goes back to its own layer
```

Effects: `static`, `rainbow`, `cycle`, `breathing`, `wave`, `gradient`, `reactive`, `ripple`, `le` (an LE lighting file), `custom`, `off`
(options: `--color`, `--color2`, `--speed`, `--dir 0-3`).

Diagnostics: `gk6x list` shows the keyboard interfaces and whether they can be opened, and `gk6x probe` lights every key
for a few seconds to test the connection.

Key mapping and macros from the terminal: run `gk6x` with no arguments for the original interactive prompt (`map`,
`unmap`, `dumpkeys`, `findkeys`...). Your files live in `~/.config/gk6x/UserData/`. Command-line switches such as `/map`
and `/p` are listed in [README-CommandlineParameters.md](README-CommandlineParameters.md).

## Control page

`gk6x ui` (or **GK6X Lighting** in the app menu) opens the control page. It has four tabs, modelled on the official
software's screens, and works in English and Portuguese with seven themes (Midnight, Neon, Carbon, Aurora, Clean light,
Material You and Classic). The theme picker is at the top right, or open `http://127.0.0.1:6465/?theme=aurora`.

**Lighting**: realtime effects, per-key painting, colors, speed and brightness. This is the live preview of what the
keyboard receives.

**Configurations**: remap keys per layer (Layer 1-3, each with its Fn layer). Pick a key, then choose a function from the
palette (Primary, Number Pad, Media, System/Net, Mouse, Disable, Layers, Lighting, Macros) or build a combination such as
Ctrl+Shift+A (Win/Mac naming toggle).
- **Save** writes `~/.config/gk6x/UserData/MODELID.txt`.
- **Apply** writes the layer to the keyboard.
- **Import / Export** exchange that file.

The standard layer is protected, as in the official software; you can only set its lighting.

![Configurations tab](docs/config.png)

**LE Files**: the 300+ lighting effects shipped with the official software, searchable.
- **View** plays one on your keyboard in realtime (a software approximation of the firmware animation).
- **Add to layer** + **Apply** stores it in the keyboard: up to 32 on the standard layer, one on each other layer.
- Paint keys on the preview and **Save** them as your own static LE file.

![LE Files tab](docs/le.png)

**Macros**: create macros with categories and search.
- **Record** captures the keys you press in the page, with timing. You can also add actions by hand (Press / Down / Up
  + delay).
- Repeat modes: N times, until the key is released, or until it is pressed again.
- To assign a macro to a key, go to Configurations -> Macros, then Apply.

![Macros tab](docs/macros.png)

Extra key functions in the palette (all firmware features): next lighting effect / reactive effect, brightness and speed
up/down, lighting pause, lighting on/off, lock the Windows key, hold-to-switch layer and layer buttons.

Firmware shortcuts that work without any software (from the Epomaker / Skyloong SK61-GK61 manuals):

| Shortcut | Action |
|---|---|
| Fn+Backspace | backlight on/off |
| Fn+P / Fn+[ | brightness - / + |
| Fn+] | next mode |
| Fn+Q / W / E / R | driver layer / layer 1 / 2 / 3 |

Files:
- profile: `~/.config/gk6x/UserData/MODELID.txt` (original GK6X text format; a `.bak` copy is kept on save)
- your lighting files: `~/.config/gk6x/UserData/lighting/*.le`
- realtime settings: `~/.config/gk6x/realtime.json`

## Update

From the folder you cloned:

```sh
cd GK6X-Linux
git pull
./linux/install.sh --no-udev --autostart    # --no-udev: the rule is already installed
systemctl --user restart gk6x
```

Your settings in `~/.config/gk6x` are kept.

## Uninstall

From the folder you cloned:

```sh
./linux/uninstall.sh            # removes the program, service, menu shortcut and udev rule (keeps your settings)
./linux/uninstall.sh --purge    # also deletes ~/.config/gk6x (settings, key mappings) and the logs
```

The keyboard keeps the key mapping last written to it. To restore the defaults, use the reset key combo from its manual
or run `gk6x` -> `unmap` before uninstalling. After that you can delete the `GK6X-Linux` folder.

## Troubleshooting

| Symptom | Fix |
|---|---|
| `gk6x list` says `open=NO` | Replug the keyboard after installing; make sure you're logged in on the local desktop session. |
| Keys type nothing while the service runs | `gk6x stop` (or replug), then try `gk6x daemon --driver-keys none`. |
| `gk6x probe` shows "NO ANSWER" | Try `gk6x probe --driver-keys none` and send the output with your model name in an issue. |
| Logs | `~/.local/state/gk6x/KbLog.txt` and `journalctl --user -u gk6x` |

Full technical documentation (how realtime lighting works, the local API, development): [README-LINUX.md](README-LINUX.md).

Developing:

```sh
cd GK6X && dotnet build -c Release     # output in ../Build
cd ../Build
dotnet GK6X.dll selftest                # offline protocol + effect checks
dotnet GK6X.dll daemon --simulate       # control page without a keyboard
```

## Credits and license

- **GK6X** by [pixeltris](https://github.com/pixeltris/GK6X): the original key mapping, macro and lighting tool this
  project is built on, with contributions from Jonavin, Faalagorn, Jhair Paris, Artoghrul Gahramanli and others.
- [@wgwoods](https://github.com/wgwoods) ([gk64-python](https://github.com/wgwoods/gk64-python)): annotated GK64 firmware
  disassembly. See also [node-gk6x](https://github.com/konsumer/node-gk6x).
- Linux port, realtime lighting daemon, control page and installer: [Nergalzin](https://github.com/nergalzin).

The code is under the [MIT License](LICENSE). The data files under `Build/Data/` come from the official software and are
proprietary to jikedingzhi; they are **not** covered by the MIT license.

---

<details>
<summary>Original GK6X README (upstream, archived). It describes the old Windows / mono builds, which this project does not use.</summary>

> **Warning:** do not use the udev rule shown below. `SUBSYSTEM=="input", GROUP="input", MODE="0666"` makes every input
> device on the machine readable and writable by any local user (a keylogger risk). `./linux/install.sh` installs a safe
> rule that only touches this keyboard.


Project archived on 2026-03-05. I've made a release updating the data files to V6.0.0.76 on this date but no GUI build due to issues. This release provides compatibility with keyboards such as GK68 but hasn't been tested thoroughly. If you're having issues try the GK6X-v1.21 release instead. It's recommended to use the official software which is linked below. This project is no longer maintained as I no longer use any GK6X based keyboards.

# GK6X

This is a command-line tool for mapping keys, macros, and lighting for GK6X keyboards (GK64, GK84, GK61, etc). This can be used as an alternative to the official software ([Windows](http://www.jikedingzhi.com/downloadlist?driverID=41) / [Mac](http://www.jikedingzhi.com/downloadlist?driverID=90)).

It runs on Windows, Mac, and Linux.

## System requirements / compilation

See the [releases](https://github.com/pixeltris/GK6X/releases) page for prebuilt binaries.

<details>
<summary>.NET Framework 4+ on Windows. mono on Mac / Linux. Expand for more info.</summary>

### Windows

.NET Framework 4+ is required (should be pre-installed on Windows 7 and higher).

To compile from source you have two options:
- Run `GK6X/GK6X.bat`
- Compile using Visual Studio (C# tools required)

### Linux / Mac

[mono](https://www.mono-project.com/download/stable/) is required (most Linux package managers should list `mono-complete`).

Compiling may take several attempts depending on the version of mono. Try `xbuild GK6X.sln`, or `msbuild GK6X.sln`, or `xbuild /p:TargetFrameworkVersion=v4.5 /p:TargetFrameworkProfile=""`, or ask for help in [#4](https://github.com/pixeltris/GK6X/issues/4).

`cd` into `Build` and run with `sudo mono GK6X.exe`. Super user (`sudo`) is required on Linux ([possible hidraw issue]( https://github.com/pixeltris/GK6X/issues/3)). Or use the udev rule below.

### Linux AUR package

GK6X is available in [AUR](https://wiki.archlinux.org/title/Arch_User_Repository) as package [gk6x-bin](https://aur.archlinux.org/packages/gk6x-bin/) maintained by [@aakashhemadri](https://github.com/aakashhemadri).

### Linux `sudo` alternative (udev rule)

An alternative to using `sudo` is to set up a udev rule. Create `/etc/udev/rules.d/gk6x.rules`:

```
SUBSYSTEM=="input", GROUP="input", MODE="0666"
SUBSYSTEM=="usb", ATTRS{idVendor}=="1ea7", ATTRS{idProduct}=="0907", MODE:="666", GROUP="plugdev"
KERNEL=="hidraw*", ATTRS{idVendor}=="1ea7", ATTRS{idProduct}=="0907", MODE="0666", GROUP="plugdev"
```

Then add yourself to `plugdev` and reboot for it to take effect:

```sh
sudo adduser $(whoami) plugdev
```
</details>

## Overview

Once the program is opened you should see something like:

`Connected to device 'GK84S RBG' model:655491200 fw:v1.16`

At this point you can start using mapping your keyboard with the `map` command as seen below.

*If you don't see any output, it has failed to detect the keyboard. There isn't any console output until it finds a valid keyboard.*

## Commands

### `map`

Maps the keyboard based on the config in `UserData/YOUR_MODEL_ID.txt`. [See Sample.txt for examples of setting keys, macros, and lighting](https://github.com/pixeltris/GK6X/blob/master/Build/UserData/Sample.txt).

You can reprogram the base layer, layers 1-3, and layers 1-3 whilst the fn is held down. You can't move the fn key, but TempSwitchLayer1/2/3 can act similar to a secondary fn key. The base layer fn key set can't be mapped.

### `unmap`

Resets the keyboard to the default config. The dedicated reset keyboard key combo will be more effective; refer to your manual.


## Additional commmands

These commands generally aren't needed.

### `dumpkeys`

Lists the textual name / code name (i.e. `5` is `D5`) of the keys listed in the order that they appear on each row of your keyboard. This shows the keys represented prior to any mapping, and is just a reference.

### `findkeys`

Tool to identify key names. Used for finding broken keys and fixing data files.

### `update_data`

This is used to update the data files from the official software. This exact process needs to be re-documented ([Updating.txt](https://github.com/pixeltris/GK6X/blob/master/Build/Data/Updating.txt)).

## Additional info

- The `/p` command line arg can be used to set a prefixed file name of the config to use. For example `GK6X.exe /p test` will use `test - 655491200.txt` as the target config file (where the keyboard model id is `655491200`).
- The following commands can be used as command line args to just execute a specific command `/map`, `/unmap`, `/dumpkeys`. For example: `GK6X.exe /map`
- There's possibly some issue with certain USB slots [(#102)](https://github.com/pixeltris/GK6X/issues/102).
- There is no "apply failed" message like there is on the official software, so `map` / `umap` can fail silently.
## Related projects

- https://github.com/wgwoods/gk64-python - thanks to [@wgwoods](https://github.com/wgwoods) for his work on annotating the dissasembly of the GK64 firmware.
- https://github.com/konsumer/node-gk6x

</details>
