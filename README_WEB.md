# Smart Ticketing — ASP.NET Core MVC Web Demo

## What is included
- `SmartTicketing.Web`: ASP.NET Core MVC (.NET 8), UI in browser, linked existing Models/Data/Services code.
- `SmartTicketing.Models`, `SmartTicketing.Data`, `SmartTicketing.Services`: original .NET Framework 4.7.2 libraries retained UNCHANGED, source reused by the .NET 8 Web project via linked .cs files (not by referencing framework DLLs).
- `SmartTicketingSystem`: original WinForms project retained as archive, not necessary to run website.
- `database/`: original SQL scripts retained.

## Run website in Visual Studio 2026
1. Install **ASP.NET and web development** workload plus .NET 8 SDK.
2. Open `SmartTicketing.Web/SmartTicketing.Web.csproj` in Visual Studio (or `dotnet run --project SmartTicketing.Web`).
3. Create database by running `database/01_create_schema.sql`, then `02_seed_data.sql`, then `03_late_success_transition.sql` in SSMS (fresh DB only).
4. Update `SmartTicketing.Web/appsettings.json` connection string to your SQL Server instance. The example `Server=.\SQLEXPRESS` requires SQL Server Express and Windows Authentication.
5. Restore NuGet packages, then run website via F5 / HTTPS URL.
6. Select route → payment method → simulate APPROVED / DECLINED / NO_RESPONSE. Result is persisted in SQL Server.

## Constraints
- This is a **working-source prototype**, not tested with Visual Studio/MSBuild/SQL Server here.
- Gateway, printer, QR, and reconciliation are mocked; **no actual funds transfer**, bank-scan QR or physical ticket printing.
- 30-second timeout is represented by NO_RESPONSE, not a real timer; 120-second QR expiry is manual. Real asynchronous callbacks/webhook validation and concurrent requests need additional hardening.
- UI follows TVM flow and screen intent; `.fig` file was supplied, but pixel-identical frame reconstruction is not confirmed.
- The current application uses ASP.NET Core session cookie to bind transaction to web user session. For a deployed kiosk or public website, authentication, business-level authorization and service hardening are needed.
- Existing Web UI includes test outcome controls for a **demo only**. Never allow customers to select APPROVED themselves in real payments.
- Keep the submitted original diagram/spec artifacts separate; the implementation may prompt minor UML updates.
