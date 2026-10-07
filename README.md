# FloatBar ⚡

**Ultra-Lightweight Precision Windows System Telemetry & Background Governor**

FloatBar is a minimalist, hardware-accelerated floating HUD and background process governor for Windows. It provides real-time CPU, RAM, and Commit Charge telemetry alongside an active **Process Sentinel** that detects and purges orphaned background MCP daemons (`chrome-devtools-mcp`, `puppeteer`, `playwright`, etc.) with zero OS Aero hover collisions.

---

## ✨ Features

- **Microscopic Floating Orb**: Sleek 44px hitbox with seamless edge-magnetic snapping and multi-monitor topology support.
- **Task Manager Accurate CPU**: Low-level kernel delta calculation using `NtQuerySystemInformation(8)` across logical processor threads.
- **Physical RAM & Commit Charge**: Dual-metric saturation telemetry via Win32 `GlobalMemoryStatusEx` (`ullTotalPageFile` / `ullAvailPageFile`).
- **Process Sentinel**:
  - Automatically identifies runaway or orphaned background MCP daemons and browser automation workers.
  - Transparent pre-action inspection: displays process name, PID, and live memory footprint before taking action.
  - One-click purge mechanism with tactile state-aware animations.
- **Zero-Dependency Native Build**: Pure C# with native WPF compilation via Windows `.NET Framework 4.8` `csc.exe` (`build.bat`). No heavy runtime runtimes or external installers required.

---

## 🚀 Quick Start

### Build & Run
```cmd
build.bat
FloatBar.exe
```

### Stop
```cmd
Stop-FloatBar.bat
```

---

## 🛠️ Architecture

- **Language / Runtime**: C# 5 / .NET Framework 4.8
- **UI Framework**: Windows Presentation Foundation (WPF) with pure code-behind visual tree construction
- **Telemetry Engine**: Background thread polling with wall-clock delta tracking and single-instance named Mutex protection
- **Governance**: WMI `Win32_Process` query targeting headless Node/MCP automation instances
