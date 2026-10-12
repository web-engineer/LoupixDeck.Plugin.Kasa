# LoupixDeck.Plugin.Kasa

A [LoupixDeck](https://github.com/RadiatorTwo/LoupixDeck) plugin for TP-Link
Kasa smart plugs and power strips. It switches plugs, single sockets of a
strip or whole strips from the deck. It can also turn a plug's status light
off. Each key shows whether its plug is on, and keys update when someone
switches a plug in the Kasa app or by its button.

Everything stays on your network. The plugin talks to the plugs directly and
never goes through TP-Link's cloud.

It is built against `LoupixDeck.PluginSdk` 1.30.0 and targets .NET 10. It runs
on macOS, Linux and Windows.

Made by [**web-engineer**](https://www.web-engineer.co.uk/)
([@web-engineer](https://github.com/web-engineer)).

This is an independent project and is not affiliated with or endorsed by
TP-Link. Kasa is a trademark of its owner.

> **Tested on:** KP105 plugs and the KP303 power strip. Both of the plugs'
> protocols are also checked byte for byte against
> [python-kasa](https://github.com/python-kasa/python-kasa). Please report
> anything that behaves differently on yours, and include the model (for
> example KP303) and the firmware version shown in the Kasa app.

## Which plugs work

Kasa plugs and strips that use the Kasa app's classic protocol (the "IOT"
family). This includes the KP105, KP115, KP303, KP400, HS100, HS103, HS105,
HS110 and HS300, on both older and newer firmware.

Some recent Kasa models, such as the KP125M and EP25, use the protocol from
TP-Link's Tapo range instead. **Find plugs** lists them as "not supported
yet". Bulbs, light strips and cameras are not supported.

## What you get

| | Key | Dial press |
|---|---|---|
| Plug on/off | Switches a plug, one socket of a strip, a whole strip, or every plug. The key glows green while it is on. Can be set to on, off or toggle. | Switches on/off |
| Status light | Turns the light on the plug or strip on or off. A strip has one light for all its sockets. | Switches it |

Every command also works on a **group**. Choose *Whole strip* or *All plugs*
in the menu, or join ids with `+` in the Device field.

## Install

**From the Plugin Store.** Once the plugin is listed, it appears under
LoupixDeck → Plugins → Store and updates from there.

**Manually.** Download `kasa-<version>-any.zip` from the
[Releases](https://github.com/web-engineer/LoupixDeck.Plugin.Kasa/releases)
page and unzip it into the plugin folder:

| OS | Folder |
|---|---|
| macOS, Linux | `~/.config/LoupixDeck/plugins/kasa/` |
| Windows | `%APPDATA%\LoupixDeck\plugins\kasa\` |

Restart LoupixDeck, then enable the plugin for your device under
Plugins → Installed.

## Set up

1. Go to **Settings → Plugins → Kasa Smart Plugs** and press **Find plugs**.
   It lists every plug that answered, with its address and firmware type.
2. If a plug says **Sign-in failed**, enter the email and password you use in
   the Kasa app under **TP-Link account** and save, then press **Find plugs**
   again. Plugs on newer firmware only accept commands from the account they
   were set up with. Older firmware ignores the account.
3. If a plug is missing, it is probably on another network from this computer
   (a VPN, another subnet or a guest Wi-Fi), so the plugin's broadcasts do not
   reach it. Enter its IP address under **Plug addresses** and save. The Kasa
   app shows the address under the plug's *Device Info*. Giving the plug a
   fixed address in your router keeps it from changing.

**No plugs to hand?** Turn on **Demo plugs**. A pretend KP105 plug and KP303
strip then behave like real ones, so you can set up and try every key first.

### About the account

- Your password is stored in LoupixDeck's settings for this plugin, on this
  computer only. It is never sent anywhere, not even to the plug. The plug and
  the plugin each prove they know a one-way hash of it, mixed with random
  values that change every session.
- A plug that has never been linked to an account accepts the plugin without
  one.

## Add controls

Add a key or dial as usual and open the **Kasa** section of the command
picker. Each plug has its own folder:

- **A plug:** On/off, Turn on, Turn off, Status light on/off.
- **A strip:** a folder for each socket (On/off, Turn on, Turn off), plus
  *Whole strip* and *Status light on/off*.
- **All plugs:** On/off, Turn on, Turn off for every plug at once.

## Limits worth knowing

- **State is read only while it is on screen.** Plugs whose keys are showing
  are read every 5 seconds (configurable). A change made in the Kasa app or at
  the plug shows up on the next read.
- **Addresses can change.** The plugin looks for plugs every minute, and
  sooner when one stops answering, so it follows a plug to a new address on
  the same network. A plug listed by address that moves has to be listed again.
- Energy readings (KP115, HS110) are not shown yet.

## Testing without LoupixDeck, or without plugs

`tools/KasaProbe` is a developer tool, not something plugin users need. It is
a small command-line program that uses the same code as the plugin and prints
each plug's state before and after every control. That makes it useful when
the plugs are somewhere you can't see them, for example over a VPN.

The account comes from the `KASA_USERNAME` and `KASA_PASSWORD` environment
variables, which keeps it out of your shell history.

```sh
read -r KASA_USERNAME && read -rs KASA_PASSWORD && export KASA_USERNAME KASA_PASSWORD
dotnet run --project tools/KasaProbe -- discover                    # broadcast on this network
dotnet run --project tools/KasaProbe -- discover 192.168.1.40       # …plus an address to ask directly
dotnet run --project tools/KasaProbe -- info 192.168.1.40           # model, firmware type, sockets, state
dotnet run --project tools/KasaProbe -- on 192.168.1.41 2           # socket 2 of a strip (or a socket name)
dotnet run --project tools/KasaProbe -- toggle 192.168.1.40
dotnet run --project tools/KasaProbe -- led 192.168.1.41 off
```

`simulate` runs pretend plugs on this computer until Ctrl+C. They are an
older-firmware KP105 on 127.0.0.1 and, given one of this computer's own
addresses, a newer-firmware KP303 on that address. The KP303 accepts the
account in `KASA_USERNAME`/`KASA_PASSWORD`, or Kasa's setup account if those
are not set. List the addresses under **Plug addresses** to drive them from
LoupixDeck.

```sh
dotnet run --project tools/KasaProbe -- simulate 192.168.1.230
```

The KP303 needs port 80. On macOS an ordinary user can listen on it, but on
Linux it needs extra permissions.

## Development

```sh
dotnet build                                    # builds and copies into ~/.config/LoupixDeck/plugins/kasa
dotnet build -p:LoupixDebugHost=true            # …into a Debug host's plugin folder instead
dotnet test tests/LoupixDeck.Plugin.Kasa.Tests
```

`LoupixDeck.PluginSdk` 1.30.0 is not on nuget.org yet, so `nuget.config`
restores it from a LoupixDeck checkout next to this repository. Build that
checkout once with
`dotnet build ../LoupixDeck/LoupixDeck.PluginSdk -c Release`.

Layout:

- `Kasa/`: the Kasa layer, which has no host dependency. It holds both
  transports (XOR on TCP 9999 for older firmware, KLAP on HTTP 80 for newer),
  discovery, sysinfo parsing, the demo plugs and `PlugService` (state cache,
  per-device request ordering and polling).
- `Commands/`: the two commands.
- `Rendering/PlugKey.cs`: how the keys look.
- `KasaPlugin.cs`: settings, the device menu and the device cache.
- `tools/KasaProbe/Simulation/FakeKasaDevice.cs`: the pretend plug, written
  from the device's side of both protocols. The tests use it in memory and
  `simulate` runs it on real sockets.

The protocol tests pin the plugin to bytes generated by python-kasa 0.11 with
fixed seeds. If you change the transports, regenerate the vectors the same way
rather than editing them.

Command names (`Kasa.Power`, `Kasa.Led`), their parameter order and the
settings keys are stored in users' profiles. Never rename or reorder them.

## Support

- Help: our public Slack and our tracker are linked from
  [web-engineer.co.uk/contact](https://web-engineer.co.uk/contact).
- Bugs and feature requests:
  [GitHub issues](https://github.com/web-engineer/LoupixDeck.Plugin.Kasa/issues).
- Security issues: please report them privately; see [SECURITY.md](SECURITY.md).

## Licence

MIT. Copyright (c) 2026 Web-Engine Limited, trading as web-engineer.
