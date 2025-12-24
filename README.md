# Speed Test

Test your internet speed from Flow Launcher using the Ookla Speedtest CLI.

---

## Install ✅

- Copy and paste the following command into Flow Launcher:

    ```bash
    pm install Speed Test by swopnil7
    ```

**Or To Manually Install:**

1. Download the latest release from [GitHub Releases](https://github.com/swopnil7/Flow.Launcher.Plugin.SpeedTest/releases).
2. Extract the ZIP file — it contains a single folder named `Speed Test-<version>`.
3. Copy the `Speed Test-<version>` folder into `%APPDATA%\FlowLauncher\Plugins`.
4. Restart Flow Launcher to load the plugin.

---

## Usage ▶️

- Open Flow Launcher and type `st` to start a speed test.
- The plugin shows live progress, final results, and a link to detailed results when available.

---

## Troubleshooting ⚠️

- If the plugin does not appear after installation, restart Flow Launcher and confirm the folder exists under `%APPDATA%\FlowLauncher\Plugins`.

---

## Credits 🙏

This plugin uses the Ookla Speedtest CLI to run network tests. Thanks to Ookla for providing the CLI — see https://www.speedtest.net/apps/cli for details and licensing.

---

## Contributing 🤝

- To build and test locally: `dotnet build -c Release` and `.
\build.ps1 -Install` (installs to your local Flow Launcher plugin path).
- Pull requests, issues, and improvements are welcome — please follow standard GitHub flow.

---

## License

MIT
