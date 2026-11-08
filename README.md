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

## BGK-3D-Text (ab 2026.1.36)

Neue BGK-3D-Text-Familien erhalten `AVA_Anzahl` als lokalen
Exemplar-Familienparameter vom Typ Zahl mit Standardwert 1. Dafür wird weder
eine gemeinsame Parameterdatei noch eine projektweite BGK-GUID benötigt.
Bereits vorhandene Familien mit dem früheren gemeinsam genutzten Parameter
werden beim Ergänzen nicht stillschweigend konvertiert; deren Identität und
vorhandene Exemplarwerte bleiben geschützt. Ein Projektparameter mit demselben
Namen kann für neue lokale BGK-Familien bestehen bleiben; bei mehrdeutigen
Parametern vorhandener Exemplare wird das Neuladen sicher abgebrochen.

## BGK-Schreibschutz (2026.1.40)

Das Schloss im BGK-Manager schaltet den Schreibschutz für das gewählte Element
**und sämtliche Unterelemente** um. Der Zustand wird mit der BGK-XML-Datei
gespeichert; nach dem Umschalten die Datei speichern. Geschützte Einträge
können nicht umbenannt, bearbeitet, verschoben, kopiert oder gelöscht werden.
Auch ein ungeschützter übergeordneter Eintrag kann nicht mitsamt geschützten
Unterelementen entfernt oder verschoben werden. Das Schloss kann den Schutz
für den gewählten Teilbaum wieder aufheben. Bei geschütztem Vorfahren muss
zuerst dessen Schloss geöffnet werden; darunterliegende Elemente bleiben
sonst weiterhin schreibgeschützt.

## Typmarkierungen gesammelt auswählen (2026.1.41)

Über **Alle markieren** und **Auswahl aufheben** oberhalb der Typenliste werden
alle Typmarkierungen der gerade gewählten Baugruppe gemeinsam ausgewählt bzw.
abgewählt. Andere Baugruppen bleiben unberührt; die Auswahl ist keine Änderung
an der gespeicherten BGK-Datei.

## Gespeicherte 3D-Ansichten sortieren (2026.1.42)

Im Fenster für gespeicherte SectionBox-Snapshots lässt sich die Liste nach
Namen **A–Z** oder **Z–A** sortieren. Die Sortierung ändert nur die Anzeige,
nicht die gespeicherten Ansichten; der ausgewählte Eintrag bleibt beim
Umschalten erhalten.

## AVA-Vorlagen und abgeleitete Prüfliste (2026.1.43)

Direkt links von **Info** befindet sich **AVA → Vorlagen verwalten**.
Der dateibasierte Editor benötigt keine Modelländerungen. Er bietet:

- neue, öffnungsfähige und als JSON importierbare/exportierbare Vorlagen;
- read-only Import von BGK-XML, GAEB DA82/3.3 (`.x82`) und Revit-Parameter-TXT;
- durchsuchbare BGK-/Typ- und LV-Kataloge, manuelle Zuordnungen und deren Bearbeitung;
- typisierte UND-Bedingungen mit Vergleichsoperator, Wert und expliziter Einheit;
- Mengenquellen mit GUID oder ausdrücklich lokalem Familienparameter,
  Spezifikation und Instanz-/Typ-Geltung;
- automatisch abgeleitete Prüfvorschriften sowie Validierung der Vorlage.

**Ablauf:** Profil benennen und unabhängigen Prüfumfang beschreiben, die drei
Quellkataloge importieren, Zuordnungen hinzufügen, Bedingungen und Mengenquelle
festlegen, anschließend die Tabs **Abgeleitete Prüfliste** und **Validierung**
prüfen und die Vorlage speichern. Für andere Stamm-LVs separate Vorlagen anlegen.
**JSON importieren** übernimmt eine portable Vorlage als ungespeicherte Kopie
mit derselben Profil-ID; **Neu** erzeugt eine neue Profil-ID.

Schema 1 enthält Katalogsnapshots, Dateinamen und SHA-256-Quellnachweise,
jedoch keine absoluten Quellpfade. Kataloge werden in dieser ersten Version
vollständig eingebettet; Weitergabe nur mit entsprechenden Nutzungsrechten.
Die Prüfliste wird nach Änderungen und beim Öffnen aus den gespeicherten
Regeln neu abgeleitet, nicht als unabhängig bearbeitbare zweite Liste gepflegt.
Unbekannte Schemas, fehlende Pflichtfelder, ungültige Referenzen und
widersprüchliche Parametertypen werden abgelehnt, ohne den aktiven Stand zu ersetzen.
Unvollständige Mengenquellen oder ein noch leerer Prüfumfang erscheinen als
Entwurfshinweise. Ungespeicherte Änderungen werden vor Verwerfen abgefragt.

Die Identifikationsvorgaben entsprechen **Übertragen**: BGK aus `ASSEMBLY_CODE`,
Typnummer aus `ALL_MODEL_TYPE_COMMENTS`, jeweils am Typ. Ein lokales
`AVA_Anzahl / NUMBER` ist ausdrücklich nicht dasselbe wie ein gemeinsamer
Parameter gleichen Namens mit `INTEGER`; es gibt keinen stillen Fallback.

**Grenzen:** Die Prüfliste beschreibt Anforderungen, nicht bereits ermittelte
Modellfehler. Der Prüfumfang ist bisher eine Beschreibung, noch kein ausführbarer
Kategorienfilter. Keine Modellprüfung, Parameterbindung, Mengenermittlung,
Umrechnung/Rundung/Aggregation und kein X31-Export. Keine Prioritäten oder
Else-Regeln; mehrere Zuordnungen werden zur fachlichen Prüfung gekennzeichnet.
Keine vollständige GAEB-XSD-Validierung. Unterstützte Mengenspezifikationen:
Zahl/Ganzzahl, Länge, Fläche, Volumen, Winkel und Massendichte; nicht jede
LV-Einheit ist damit bereits abgedeckt.

Automatisierte AVA-Tests einschließlich WPF-Konstruktion/Layout:

```bash
dotnet run --project tests/AvaTemplateTests/AvaTemplateTests.csproj
dotnet run --project tests/AvaRibbonTests/AvaRibbonTests.csproj
```

Optional kann dem ersten Testprojekt der Ordner mit den drei Referenzdateien
als Argument übergeben werden. Es prüft deren Import und einen technischen
JSON-Roundtrip; die Testzuordnung ist keine fachlich freigegebene LV-Zuordnung.
Die Tests ersetzen nicht den interaktiven Test des Befehls in Revit.

## Geometrie aus gewählten Flächen (2026.1.37)

Im Menü **Geometrien** lassen sich einzelne begrenzte Revit-Flächen auswählen.
Eine Familie wird nur für einen geprüften, geschlossenen Volumenkörper geladen
und platziert. Beim Halbraum-Verfahren werden vor der Konstruktion
Schnittsegmente aus triangulierten Flächen ermittelt und jede Ergebnisfläche
und gemeinsame Kante der Auswahl zugeordnet. Nur für dieses Verfahren gilt:
Gekrümmte Flächen werden facettiert; 1 mm ist die maximal zugelassene
Abweichung **der geprüften Stützpunkte**, keine mathematisch garantierte
maximale Flächenabweichung.

Ein neuer Pfad rekonstruiert aus einer **durchgehend rotationssymmetrischen**
Außenkontur einen gefüllten Volumenkörper: konische und zylindrische Mäntel,
auch von verschiedenen Elementen, sowie passende waagerechte Ringabsätze.
Zwei ebene Stirnflächen werden an den eindeutig bestimmten Enden ergänzt;
Innenwände und innere Öffnungen werden dabei **nicht** übernommen. Die
ausgewählten Mantelflächen müssen gemeinsam über ihre gesamte Höhe den
vollständigen Umfang abdecken. Achse, Radialprofil, umlaufende Kreisränder und
auswärts gerichtete Flächennormalen werden geprüft; unklare Lücken und von der
Außenkontur abweichende Flächen werden abgelehnt. Das ist **kein** allgemeiner
Algorithmus für beliebige gekrümmte Freiformflächen oder mehrere Achsen.
Der neue Pfad ist bislang nur gebaut und statisch getestet, **nicht** für den
konkreten Schacht in Revit abgenommen.

Sind alle Flächen eines einzelnen, bereits geschlossenen Volumenkörpers gewählt,
werden sie exakt mit dessen Flächen verglichen und der vorhandene Körper
einschließlich seiner Öffnungen und Innenwände übernommen. Außerhalb des
neuen rotationssymmetrischen Pfads wird bei anderen Mehrfach-Randflächen nur
eine bereits durch die ausgewählten Flächen
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

## Schnittmarker auf Plänen (2026.1.39)

Unter **Pläne → Schnitte bereinigen** kann der aktuelle Plan oder das gesamte
Projekt ausgewählt werden. In den platzierten Grundriss-, Decken-, Ingenieur-,
Schnitt- und Ansichts-Views werden Schnittmarker ausgeblendet, wenn die zugehörige
Schnittansicht nicht auf demselben Plan liegt; bereits ausgeblendete Marker zu
einem Schnitt auf demselben Plan werden eingeblendet. Die Zuordnung übernimmt
den Schnittansichtsnamen aus dem ursprünglichen Dynamo-Skript. Unbekannte oder
mehrdeutige Namen bleiben unangetastet. Ein bereits aktiver temporärer
Sichtbarkeitsmodus wird nicht vom Befehl beendet; bei einem Fehler wird die
gesamte Transaktion zurückgerollt. Build und Policy-Tests ersetzen keinen
interaktiven Revit-Test mit einem geeigneten Planmodell.