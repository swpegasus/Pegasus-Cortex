# Knowledge.Windows.Installer.Maintenance

Object: Knowledge.Windows.Installer.Maintenance
Version: 1.0.0
Triggers: Windows GUI installer, Add/Remove Programs, upgrade, feature change, repair, uninstall
Rendered budget: 5120 UTF-8 bytes

## Product identity

Maintain one stable product identity and registered install location. Register bounded Add/Remove Programs metadata including display name/version/publisher, install location, uninstall command, and maintenance/modify command when supported. Installed mode should present Modify, Repair, and Uninstall rather than pretending every run is a fresh install.

## Feature isolation

Each optional feature owns its detection, installation, repair, and removal routine. Unselected features perform no inspection or mutation beyond what is required to display their current installed state. Preserve prior selections during upgrade and offer new optional features explicitly.

## Upgrade

Detect the existing product through its owned registration and admitted layout. Warn before stopping owned components. Use Restart Manager or a product-defined close protocol where appropriate; otherwise require the exact blocking application to close. Preserve databases and user state. Replace immutable program files and reconcile registration. Verify service/user-host startup and one bounded real workflow after installation.

## Repair

Repair admitted program files and owned registration without resetting user data, preferences, databases, trust selections, or unrelated configuration. Missing mutable state is not silently replaced from a starter copy unless the product explicitly owns that recovery operation.

## Uninstall

Remove only inventory-owned binaries, shortcuts, service/startup registration, and product registration. Retain databases and durable user state by default. Any state deletion is a separate explicit expert operation. Preserve shared trust or components still referenced by another installed product.

## Failure

Show the current stage, bounded actionable error, persistent log location, and recovery state. A backend exit zero must be interpreted as success; wrapper launch failure and setup failure are distinct. Do not perform unrelated end-of-install routines for unselected features.

References:
- https://learn.microsoft.com/en-us/windows/win32/msi/uninstall-registry-key
- https://learn.microsoft.com/en-us/windows/win32/msi/using-windows-installer-with-restart-manager
