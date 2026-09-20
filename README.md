# WDWAPP

A web application for Wermland Dice Warriors, a tabletop gaming club in Karlstad, Sweden.

The application provides tools for managing club activities, players, leagues, matches, events and marketplace advertisements. It is developed as a real-world project for the club and is actively being expanded.

## Tech stack

* .NET 10
* Blazor Web App with Interactive Server rendering
* ASP.NET Core Identity
* Entity Framework Core
* SQLite
* xUnit

## Features

* User registration and authentication
* Role-based access control for users, members, key members and administrators
* Player profiles with statistics and achievements
* 40K league with match reporting, standings and ELO ratings
* Matchmaking and match requests
* Calendar and event management
* News publishing
* Member marketplace
* Administrative user and content management

## Security

The application uses ASP.NET Core Identity and server-side authorization.

Security measures include:

* Policy-based role authorization
* Server-side ownership and permission checks
* Antiforgery protection
* Account lockout after repeated failed login attempts
* Secure and HttpOnly authentication cookies
* Security stamp validation after account or role changes
* File type validation based on file signatures
* Size restrictions for uploaded images
* Concurrency protection for sensitive database operations

## Testing

The project includes automated tests covering authentication, authorization, user administration, league functionality, player profiles, match requests, marketplace functionality and other application features.

## Running locally

Requirements:

* .NET 10 SDK

Clone the repository and run:

```bash
dotnet restore
dotnet run
```

The application uses a local SQLite database. In the Development environment, Entity Framework Core migrations are applied automatically.

The local database is excluded from source control.

## Project status

The core application and main functionality are implemented. Development is ongoing, with additional functionality and UI improvements planned.
