# מבחן הזמנים — English Tenses Practice (ASP.NET Core / IIS-native)

Same app and same login as before, but the backend is **ASP.NET Core** — no Node.js.
It serves the static page and exposes the login + a secure Claude proxy. The API key and the
password check live only on the server.

```
Program.cs            the whole backend (static hosting + /api/login,/me,/logout,/claude)
TensesSite.csproj     net8.0 web project
appsettings.json      config (leave the key empty here; set it via env / web.config)
wwwroot/index.html    the app (unchanged front-end)
```

## Prerequisites
- To build: **.NET 8 SDK** (on your dev machine or the server).
- On the IIS server: the **ASP.NET Core 8 Hosting Bundle**
  (https://dotnet.microsoft.com/download/dotnet/8.0 → "Hosting Bundle"). This installs the
  ASP.NET Core Module for IIS. Run `iisreset` after installing it.

## Run locally
```bash
dotnet run
```
Set config first, e.g. (PowerShell):
```powershell
$env:ANTHROPIC_API_KEY="sk-ant-..."; $env:SESSION_SECRET="a-long-random-string"; dotnet run
```
Then open the URL it prints (e.g. http://localhost:5000).

## Publish & deploy to IIS
1. Build a publish folder:
   ```bash
   dotnet publish -c Release -o publish
   ```
   This also generates a `web.config` wired to the ASP.NET Core Module.
2. Copy the **publish/** folder to your IIS site's physical path.
3. In IIS Manager: create the site/app and set its **Application Pool → .NET CLR version = "No Managed Code"** (ASP.NET Core runs out-of-CLR via the module).
4. Give the app-pool identity read access to the folder.
5. **Set the API key + login secret.** Easiest: edit the generated `web.config` and add an
   `environmentVariables` block inside `<aspNetCore>`:
   ```xml
   <aspNetCore processPath="dotnet" arguments=".\TensesSite.dll" hostingModel="inprocess">
     <environmentVariables>
       <environmentVariable name="ANTHROPIC_API_KEY" value="sk-ant-..." />
       <environmentVariable name="SESSION_SECRET" value="a-long-random-string" />
       <environmentVariable name="APP_USER" value="נוגה" />
       <environmentVariable name="APP_PASS" value="סהר" />
     </environmentVariables>
   </aspNetCore>
   ```
   (Alternatively use system/app-pool environment variables, or an `appsettings.Production.json`
   that you keep out of source control.)
6. Browse the site. You'll get the login screen (נוגה / סהר), then the practice test.

## Login
- Checked on the server; the password is never in the browser, and `/api/claude` refuses requests
  without a valid session — so no one can use your API key anonymously.
- Change credentials via `APP_USER` / `APP_PASS`. Set a long random `SESSION_SECRET` so logins
  survive app restarts. Sign out via `POST /api/logout`.

## Protecting your bill
- **Set a monthly spend limit** in the Anthropic Console (Billing) — your safety net.
- The proxy fixes the model & `max_tokens` server-side and rate-limits per IP (40 Claude calls/min,
  15 login attempts/10 min). Tune in `Program.cs`.
- Set `ALLOWED_ORIGIN` to your domain. `claude-haiku-4-5-20251001` is the default (fast & cheap);
  change via `ANTHROPIC_MODEL`.
- Confirm current model names / API version at https://docs.claude.com/en/api/overview
