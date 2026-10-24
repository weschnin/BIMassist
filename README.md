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

## Geometrie auf umschlossenen Flächen (Entwicklungsstand 2026.1.35)

Im Menü **Geometrien** lassen sich einzelne begrenzte Revit-Flächen auswählen.
Eine Familie wird nur für einen geprüften, geschlossenen Volumenkörper geladen
und platziert. Beim Halbraum-Verfahren werden vor der Konstruktion
Schnittsegmente aus triangulierten Flächen ermittelt und jede Ergebnisfläche
und gemeinsame Kante der Auswahl zugeordnet. Nur für dieses Verfahren gilt:
Gekrümmte Flächen werden facettiert; 1 mm ist die maximal zugelassene
Abweichung **der geprüften Stützpunkte**, keine mathematisch garantierte
maximale Flächenabweichung.

Sind alle Flächen eines einzelnen, bereits geschlossenen Volumenkörpers gewählt,
werden sie exakt mit dessen Flächen verglichen und der vorhandene Körper
einschließlich seiner Öffnungen und Innenwände übernommen. Bei anderen
Mehrfach-Randflächen wird nur eine bereits durch die ausgewählten Flächen
vollständig geschlossene, facettierte Hülle versucht; offene Kanten oder
fehlende Innenwände werden abgewiesen, nicht ergänzt. Die ursprüngliche
Halbraum-Rekonstruktion bleibt auf geeignete konvexe Flächen ohne innere
Öffnungen beschränkt. Koplanar überlappende Flächen und sehr komplexe
Facettierungen werden weiterhin mit Diagnose abgelehnt. Für allgemeine
konkave oder mehrfach gekrümmte Flächenschnitte wäre ein zusätzlicher
Geometriekern mit gesonderter Lizenz-/Installationsprüfung erforderlich.
Die Familienbenennung folgt erst nach erfolgreicher Geometrieprüfung; bei
nicht unterstützter Geometrie erscheint eine Diagnose statt zweier Fehlerdialoge.
Auf einer Kopie von `TestProjekt_2026.rvt` wurden in Revit 2026 die sieben
tatsächlich gewählten Flächen exakt einem geschlossenen Quellkörper zugeordnet.
Eine daraus erzeugte Familie behielt ihre sieben Flächen und die Fläche mit
zwei Rand-Loops nach Speichern, erneutem Öffnen und Laden ins Projekt;
die Platzierung gelang in einer danach zurückgerollten Testtransaktion.
Der interaktive BIMassist-Befehl wurde mit dieser Auswahl vom Anwender in
Revit 2026 geprüft: Die Familie wurde erzeugt, die Öffnung blieb erhalten
und die Familie ließ sich platzieren. Andere Geometrien sind dadurch nicht
pauschal abgenommen.