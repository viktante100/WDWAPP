# Sök match och kalenderkategorier

`Member` är befintligt terminspass. Policyn omfattar även `KeyMember` och
`Admin`. `/sok-match` och redigering kräver denna policy; kalender och
lediga matchförfrågningar är offentliga. När en match bokas syns den i kalendern
bara för de två deltagarna. Andra användare får 404 även vid direkta länkar.
Admin har dessutom läsåtkomst via hanteringsvyerna för att kunna ta bort bokningar.
Kalendern filtrerar också dagens färgprickar och antal så att privata bokningar inte
avslöjas. Flera synliga händelser samma dag visas som separata kort under kalendern,
med färgprickar per händelsetyp i datumrutan. Startsidan använder **Boka match** och menyn
**Sök match**. `/bokning` fungerar också som ingång.

`MatchRequestService` kontrollerar autentiseringens security stamp och aktuella
databasroller för varje ändring. Ägare och motspelare hämtas via kontots
identitet. Formulären innehåller inga identitetsfält. Nickname kommer från
`ApplicationUser.UserName`. Member, KeyMember eller Admin räcker;
ingen spelarprofil krävs för att skapa eller acceptera.
Spelalternativen finns i `MatchGames.All` och kan utökas där.

## Data och migrering

`MatchRequest` länkar ägarkonto, eventuellt motspelarkonto och en kalenderpost.
Datum och svensk lokal starttid lagras bara i kalenderposten. Spel och
acceptanstid i UTC lagras i förfrågan. Nickname kopieras inte, utan läses från
kontona också efter en namnändring. En unik FK från `Events.MatchRequestId`
förhindrar dubbla kalenderposter. Borttagning av förfrågan tar bort dess
kalenderpost i databasen. Kontoborttagning rensar relaterade förfrågningar och kalenderposter.
Spelarprofilen är oberoende: ändring eller borttagning av den påverkar inte bokningen.

`LinkMatchRequestsToAccounts` flyttar befintliga matchrelationer från spelarprofiler
till deras användarkonton och bevarar bokningar, acceptanstider och kalenderlänkar.
Migreringen kan inte automatiskt rullas tillbaka eftersom nya deltagare kan sakna
spelarprofil; återställ en säkerhetskopia från före migreringen om rollback krävs.

Migreringen `AddMatchRequestsAndEventTypes` bevarar datum, innehåll, nyhetslänkar
och ligalänkar. Befintliga poster med `LeagueSessionId` blir `LeagueRound`.
Övriga befintliga poster blir `GameDay` (Speldag); ingen kategori gissas från
rubriken. Administratören kan sedan ändra en manuell post till Turnering.
Databaskontroller begränsar kategorierna och kräver rätt systemrelation.

Utvecklingsmiljön migrerar automatiskt som tidigare. I produktion: säkerhetskopiera
SQLite-databasen och kör `dotnet ef database update` före start av den nya versionen.
Det tillämpar även eventuella tidigare väntande migreringar. Ingen e-postkonfiguration behövs.

## Kalender och samtidighet

Manuellt skapade evenemang kräver ett uttryckligt val av Turnering eller Speldag.
Ligaomgång och Matchsök skapas bara av respektive tjänst. Befintlig behörighet
för evenemangsredaktörer (KeyMember/Admin) är bevarad.
`CalendarPresentation` samlar namn och CSS-klasser: Matchsök turkos,
Ligaomgång blå, Turnering guld och Speldag lila. Legend och etiketter kompletterar
färgerna. Ligasynkningen fungerar som tidigare.

Acceptans sker i en SQLite-skrivtransaktion och en villkorad `UPDATE` som kräver
oaccepterad status och samma versionsnyckel som användaren såg. Exakt en
uppdaterad rad krävs för framgång. Acceptans och borttagning/redigering kan
inte passera varandra. Ändrat datum/spel kräver omladdning före acceptans.
En passerad starttid kan inte accepteras. Ogiltiga eller tvetydiga lokala tider
vid sommar-/vintertidsbyte avvisas vid skapande/redigering.

Efter commit returneras `MatchAccepted` från tjänsten som en del av resultatet.
En framtida applikationshanterare kan använda detta för avisering; ingen SMTP,
extern leverantör eller notifieringscentral har införts. Händelsen är inte en
beständig leveranskö. Om garanterad framtida leverans behövs kan en outbox
skrivas i samma transaktion. Manuella nyhets-/evenemangsaviseringar kan senare
använda samma applikationsgräns utan att ändra matchreglerna.

## Kontroller

Riktade tester finns i `MatchRequestTests`, `EventTests` och ligatestet
`Round_calendar_is_linked_synchronized_and_never_published_as_news`.
De täcker bland annat roller utan spelarprofil, manipulerad identitet, antiforgery,
publik kalender, samtidiga accepteringar, ägarskap, synkning och migrering av
befintliga poster. Kontrollera gärna också flödet visuellt på mobil och dator:
skapa som medlem, öppna kalendern utloggad, acceptera med en annan medlem och
kontrollera att ägaren ser båda nickname i sin lista.
