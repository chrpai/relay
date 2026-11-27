# relay

A telnet relay for BBSes that can't accept incoming connections, such as a Commodore 64 BBS on a C64 Ultimate (its UCI network interface can only dial out), or any BBS behind CGNAT.

The BBS dials out to the relay and waits. When a caller connects to the relay, the relay sends the BBS a `CONNECT` line and passes bytes both ways until either side hangs up. Then the BBS dials back in for the next caller.

It is single-line: one caller at a time. It runs as a Windows service, with its settings in `appsettings.json`.

## What callers see

| Situation | Caller gets |
| --- | --- |
| BBS is connected and waiting | The `Connected` message, then the BBS |
| BBS is on a call with someone else | The `Busy` message, then disconnect |
| BBS isn't connected | Waits up to `ReconnectGraceSeconds` for it to redial, then the `Offline` message and disconnect |

The grace period covers the second or two the BBS takes to redial after a call, so a caller who arrives right then is joined instead of being told the BBS is offline.

## What the BBS sees

1. It opens a TCP connection to `BbsPort`. Nothing is sent while it waits.
2. When a caller is joined, the relay sends `CONNECT` and CR LF. Everything after that is the caller's bytes, untouched: telnet negotiation is passed through for the BBS to handle. With telnet detection on (below), the line is `CONNECT TELNET` or `CONNECT RAW` instead.
3. If the caller hangs up, the relay closes the BBS connection. If the BBS closes its connection, the relay hangs up the caller.
4. The BBS dials in again to wait for the next caller.

If a second BBS connection arrives while one is already waiting, the new one replaces the old one, so a C64 that resets and redials doesn't leave a dead connection in the way. The relay uses TCP keepalive to notice a BBS or caller that disappears without closing (power off, dropped router connection) within about 45 seconds.

## Modem link (Image BBS on a C64 Ultimate)

A BBS can instead have the relay act as its modem. It says so by sending $FF $02 as soon as it connects; the Image BBS Ultimate Net driver does this. The relay then:

- answers the BBS's AT commands (`ATZ`, `ATE0V0`, `ATS0=1`, `ATA`, `ATH`, `ATI`; anything else gets OK), with numeric or word results (`V0`/`V1`);
- when a caller arrives, raises carrier and sends `CONNECT` (with `S0=0` it sends `RING` and waits for `ATA` for up to 30 seconds);
- like a real modem, doesn't answer while the BBS is giving it commands: a caller is held until the BBS has sent nothing for `AnswerQuietMilliseconds`, and after a call also until the BBS has reset and set up its modem again (up to `ReinitWaitSeconds`). A caller who rings back straight away doesn't see the BBS's `ATZ` and init strings;
- dials out for `ATD[T|P]host[:port]` (port 23 by default) and answers `CONNECT` or `NO CARRIER`, so Image Term and network mail work through it;
- handles telnet for callers it found to be telnet clients (with `TelnetDetectMilliseconds` on): it negotiates echo, suppress-go-ahead and binary mode, strips telnet commands, and doubles $FF toward the caller. The BBS sees plain bytes.

On the link, bytes go both ways with $FF doubled. Escapes: from the relay, $FF $01 is carrier on and $FF $00 carrier off; from the BBS, $FF $00 means DTR dropped, which hangs up the call.

While the link is on a call or dialing out, other callers get the `Busy` message. Plain BBS connections (no hello) work as before.

## Install

Run `Relay-<version>.msi`. It installs to `C:\Program Files\ISWIX LLC\Relay` and registers the `TelnetRelay` Windows service, which starts automatically, both now and at boot.

The installer doesn't open the firewall yet. From an elevated PowerShell:

```powershell
New-NetFirewallRule -DisplayName "Telnet Relay (callers)" -Direction Inbound -Protocol TCP -LocalPort 23 -Action Allow
New-NetFirewallRule -DisplayName "Telnet Relay (BBS)" -Direction Inbound -Protocol TCP -LocalPort 6400 -Action Allow
```

To limit the BBS port to your BBS's address, add `-RemoteAddress <its IP>` to the second rule.

Connections, hangups, and busy and offline callers are logged to the Windows Event Log (Windows Logs > Application, source `TelnetRelay`).

## Settings

`appsettings.json`, in the install folder:

```json
{
  "Relay": {
    "CallerPort": 23,
    "BbsPort": 6400,
    "ReconnectGraceSeconds": 5,
    "TelnetDetectMilliseconds": 0,
    "AnswerQuietMilliseconds": 1000,
    "ReinitWaitSeconds": 30,
    "Messages": {
      "Connected": "Connecting...\r\n",
      "Busy": "The BBS is busy with another caller. Please try again soon.\r\n",
      "Offline": "The BBS is offline right now. Please try again later.\r\n"
    }
  }
}
```

| Setting | Default | Meaning |
| --- | --- | --- |
| `CallerPort` | 23 | Port callers connect to |
| `BbsPort` | 6400 | Port the BBS dials out to |
| `ReconnectGraceSeconds` | 5 | How long a caller waits for the BBS to redial before getting `Offline` |
| `TelnetDetectMilliseconds` | 0 | How long to watch a new caller for telnet negotiation. 0 turns detection off and the BBS gets plain `CONNECT` |
| `AnswerQuietMilliseconds` | 1000 | Modem link: a caller is answered only once the BBS has sent no modem commands for this long |
| `ReinitWaitSeconds` | 30 | Modem link: after a call, the longest a caller waits for the BBS to set up its modem again |
| `CarrierToConnectMilliseconds` | 500 | Modem link: time between raising carrier and sending `CONNECT`, so Image sees the carrier before it checks for it |
| `TraceData` | false | Modem link, for troubleshooting: logs every chunk of call data in hex, both ways. Busy; leave it off normally |
| `Messages.Connected` | `Connecting...` | Sent to the caller just before they're joined. Empty sends nothing |
| `Messages.Busy` | see above | Sent when the BBS is on another call |
| `Messages.Offline` | see above | Sent when the BBS isn't connected |

Messages are sent one byte per character, so `\r\n` line endings and `\u00XX` escapes (for PETSCII or CP437 codes) work. Restart the service after changing settings:

```powershell
Restart-Service TelnetRelay
```

Installing a newer version currently replaces `appsettings.json` with the default, so copy yours somewhere safe before upgrading.

### Telnet detection

Telnet clients (SyncTERM, PuTTY, Windows `telnet`) send telnet negotiation the moment they connect. Raw TCP clients, such as most C64 Wi-Fi modems and the C64 Ultimate's modem emulation, send nothing. With `TelnetDetectMilliseconds` above 0 (300 is a good start), the relay waits that long for the caller's first bytes and tells the BBS what it found:

- `CONNECT TELNET` when the first byte is IAC ($FF), so the BBS can answer the negotiation, undouble $FF bytes and double its own.
- `CONNECT RAW` when the caller sent nothing or something else, so the BBS leaves the data alone.

Whatever the caller sent while the relay was watching is passed to the BBS right after the line, so nothing is lost. A telnet client that waits for the server to negotiate first is reported as `RAW`, which is the same as the relay's behaviour with detection off.

The BBS port has no password. Anyone who finds it can connect and receive the next caller, so if the relay is on the internet, limit that port to your BBS's IP with the firewall rule above.

## Repository layout

| Folder | Contents |
| --- | --- |
| `Application\TelnetRelay` | The service (.NET 10) |
| `Application\TelnetRelay.Tests` | Scenario tests |
| `Application\tools` | `fake_bbs.py`, a stand-in BBS for testing |
| `Installer\RelayPackage` | The IsWiX / WiX installer project |
| `Installer\Deploy` | Publish output the installer packages (not in source control) |

## Build

Needs the .NET 10 SDK, and Visual Studio with the IsWiX extension for the installer.

1. Open `Application\Application.sln` and publish `TelnetRelay` with the `FolderProfile` profile. It publishes a self-contained win-x64 build to `Installer\Deploy`. From the command line:

   ```powershell
   dotnet publish Application\TelnetRelay -p:PublishProfile=FolderProfile
   ```

2. Open `Installer\Installer.slnx` and build `RelayPackage`. The MSI lands in `Installer\RelayPackage\bin\<platform>\<configuration>`. Pass `-p:MSIProductVersion=x.y.z` for a release build; local builds are `0.0.1`.

Publish again before building the installer whenever the application changes, so `Installer\Deploy` is current.

## CI and releases

GitHub Actions builds on Windows with the .NET 10 SDK. Both workflows run the tests, publish the service to `Installer\Deploy` and build the MSI (`.github/workflows/build.yml`).

- **CI** (`ci.yml`) runs on every push to `main` and every pull request. The MSI is versioned `0.0.<run number>` and attached to the run as the `relay-msi` artifact.
- **Release** (`release.yml`) runs only when you start it: Actions > Release > Run workflow, on `main`. Pick `patch`, `minor` or `major` to increment the latest `vX.Y.Z` tag, or pick `custom` and type a version such as `1.4.0`. It checks that the version is higher than the last release and within Windows Installer's limits (255.255.65535), stamps it on `TelnetRelay.exe` and the MSI, then creates the `vX.Y.Z` tag and a GitHub release with the MSI attached and generated release notes. The first release starts from 0.0.0.

## Try it without a C64

Run the relay in a console:

```powershell
dotnet run --project Application\TelnetRelay
```

In another window, start the stand-in BBS:

```powershell
python Application\tools\fake_bbs.py 127.0.0.1 6400
```

Call it with SyncTERM or `telnet 127.0.0.1 23`. It greets you and echoes what you type; ESC or typing BYE hangs up.

## Tests

```powershell
dotnet run --project Application\TelnetRelay.Tests
```

These are scenario tests with real sockets on local ports: offline, connected, busy, hangups from each side, the reconnect grace period, a dead idle BBS connection, binary data with 0xFF bytes passing through unchanged, and telnet detection, and the modem link (AT commands, answering, dialing out, telnet callers, hang-ups both ways, holding a caller while the BBS sets up its modem).

## License

MIT
