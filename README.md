# ATC Planner

ATC Planner je veb aplikacija namenjena za planiranje i optimizaciju rasporeda (roстера) za kontrolore letenja.

## O projektu

Ovaj projekat se sastoji iz dva glavna dela:

*   **Backend:** .NET Web API koji se bavi poslovnom logikom, optimizacijom rasporeda koristeći OR-Tools i komunikacijom sa bazom podataka.
*   **Frontend:** Angular aplikacija koja pruža korisnički interfejs za pregled i upravljanje rasporedima.

## Tehnologije

*   **Backend:**
    *   .NET
    *   ASP.NET Core Web API
    *   Google OR-Tools
*   **Frontend:**
    *   Angular
    *   TypeScript
    *   HTML/CSS

## Pokretanje projekta

Da biste pokrenuli projekat lokalno, pratite sledeće korake:

### Backend

1.  Otvorite `backend/ATCPlanner.sln` rešenje u Visual Studio-u.
2.  Podesite konekcioni string za bazu podataka u `appsettings.json` datoteci.
3.  Pokrenite projekat (pritiskom na F5 ili `dotnet run` komandom).

### Frontend

1.  Pozicionirajte se u `frontend` direktorijum.
2.  Instalirajte zavisnosti komandom: `npm install`.
3.  Pokrenite razvojni server komandom: `npm start` ili `ng serve`.
4.  Otvorite pregledač i posetite `http://localhost:4200/`.
