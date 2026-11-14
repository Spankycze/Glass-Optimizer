# Spankyho Optimizer - graphics development toolchain

This graphics suite runs ONLY during development. No change to Directory.Build.props, .NET10, WinUI3 runtime, fan control, AI, network tweaks, current installed GUI, or browser processes.

## Components
- Penpot MCP 2.15.4: connect a user-approved Penpot design to edit through MCP; starts on demand.
- Motion 14.3.0, Lottie Web 5.13.0, Three.js 0.186.1: graphical concept prototyping; not shipped in EXE.
- Lucide Static 1.58.0: 53 icons and expandable SVG library.
- Pixelmatch 8.0.0 and PNGJS 7.0.0: image comparisons.
- Blender 5.2.2 LTS Portable: verified official download, CLI, shared local directory junction.
- Microsoft Windows App SDK Samples: Composition/Windowing references, shared local directory junction.
- Biome 2.5.15 and jsdom 30.1.2: UI static checks and DOM fixtures.

## Commands, from Optimizer project root

    npm run graphics:setup
    npm run graphics:test
    npm run graphics:penpot
    npm run graphics:compare -- "artifacts/overview_printwindow2.png" "artifacts/real_app_current.png" "devtools/graphics/generated/overview-diff.png" 0.1

Verify without downloading:

    pwsh -NoProfile -File devtools/graphics/setup-graphics.ps1 -VerifyOnly

graphics:setup installs locked local Node packages, retrieves optional missing Blender/SDK vendor files, generates graphics and runs self-checks, not the main .NET app.

graphics:test creates 53 SVG icons, generated/tokens.css (Optimizer, Light and Graphite themes), generated/themes.json, generated/penpot-artboard.svg, and runs smoke tests.

graphics:penpot starts installed Penpot MCP ONLY on demand. A Penpot account/design connection requires separate user authorization. No browser is opened automatically.

graphics:compare produces a diff PNG and JSON report with counts, exit 0 identical, 1 different, 3 different size. Screenshots must have the same window size, DPI, active section and capture timing for useful comparisons.

## Shared assets / safety
Optimizer's Blender and SDK directories are Windows junctions to existing verified local RAM Guard vendor tools: no second ~909 MiB Blender copy. If RAM Guard is moved or removed, graphics:setup can install independent versions, or update the junction target. Vendor files and generated assets are Git-ignored.

This is only a toolchain installation, NOT a production UI redesign. The 30fps value in design metadata is a future design target; no real native animation cap changed. No EXE release, version bump, profile edit or process restart was performed.

Official sources: https://github.com/penpot/penpot ; https://github.com/microsoft/WindowsAppSDK-Samples ; https://github.com/motiondivision/motion ; https://github.com/lucide-icons/lucide ; https://github.com/airbnb/lottie-web ; https://github.com/mrdoob/three.js ; https://github.com/mapbox/pixelmatch ; https://www.blender.org.
