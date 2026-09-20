# Testkörningar som inte kräver en aktiv chatt

Kör från projektmappen i en vanlig PowerShell-terminal:

```powershell
.\scripts\Test.ps1 -Background
```

Det startar en separat dold Windows-process med marknadstesterna och JavaScript-testerna. Startkommandot återkommer direkt. Processen använder ingen AI och behöver ingen fortsatt dialog med Codex. Datorn måste vara igång. En omstart, utloggning eller en miljö som avslutar underprocesser kan fortfarande avbryta körningen; start från din egen terminal ger oberoende av Codex-sessionens processhantering.

För en fullständig kontroll vid slutet av ett större arbete:

```powershell
.\scripts\Test.ps1 -Suite All -Background
```

Utelämna `-Background` om du vill vänta i terminalen och få en vanlig exitkod. Inga tester tas bort, hoppas över eller godkänns automatiskt vid fel. Kör inte flera testomgångar mot samma arbetskopia samtidigt; skriptet blockerar samtidiga körningar som startas via skriptet.

## Läs resultat utan att köra om testerna

```powershell
$testRun = (Get-Content .\artifacts\tests\latest.txt -Raw).Trim()
Get-Content (Join-Path $testRun 'status.json')
```

Varje körning får en egen mapp med `status.json`, `.NET`-resultatet `dotnet.trx`, `dotnet.log` och `javascript.log`. Status sparas även efter .NET-steget, före JavaScript-steget. `running` eller `starting` betyder inte godkänt: kontrollera process-ID och loggar om status ligger kvar efter ett avbrott. En tidigare rapport gäller koden som testades då, inte senare ändringar.

För att hålla nere Codex-användningen: kör berörda tester efter ändringar, spara långa loggar i filer, läs kort status och bara relevanta fel, återanvänd redan sparade resultat när koden inte har ändrats, och kör hela sviten en gång i slutet när det behövs. Testerna ändrar inte kontots användningsgräns. Den styrs av OpenAI: https://learn.chatgpt.com/docs/pricing#what-are-the-usage-limits-for-my-plan
