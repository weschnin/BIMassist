# BIMassist

BIMassist ist ein eigenständiges Add-in für Revit 2026.

## Abgrenzung zu RevitMCP

RevitMCP wird in einem eigenen Repository entwickelt und über ein eigenes
`.addin`-Manifest mit eigener Bridge-DLL installiert. BIMassist registriert
keine RevitMCP-Bridge und referenziert weder den MCP-Server noch dessen
Projekte oder Pakete. Auch ein expliziter BIMassist-Deploy kopiert nur
`BIMassist.dll` und `BIMassist.addin`.

Beide Add-ins können unabhängig voneinander in derselben Revit-Installation
registriert sein. Das ist eine Trennung der Projekte, Assemblies und
Registrierungen, keine Trennung in verschiedene Revit-Prozesse.