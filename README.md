# IncidentIQ

An AI-powered synthetic incident investigation workspace. Frontend and backend
are separate applications and can be built and deployed independently.

```text
IIQ\
  backend\
    IncidentIQ.sln
    global.json
    src\IncidentIQ.Api\
    data\
    scripts\
    README.md
  frontend\
    package.json
    package-lock.json
    src\
    index.html
    vite.config.ts
```

## Run the backend

In one PowerShell terminal, from `C:\Projects\IIQ`:

```powershell
cd backend
$env:DOTNET_ENVIRONMENT = "Development"
$env:ASPNETCORE_ENVIRONMENT = "Development"
dotnet run --project src\IncidentIQ.Api --no-launch-profile -- --urls http://localhost:5080
```

The .NET 10 backend serves API routes only. Existing ignored development
configuration and the saved embeddings moved with the project; re-ingestion
is not required. Azure secrets belong only in backend configuration.
The solution now opens from `backend\IncidentIQ.sln`.
Backend API/configuration/evaluation details are in `backend\README.md`;
its commands run from the `backend` directory.

## Run the frontend

In another PowerShell terminal, from `C:\Projects\IIQ`:

```powershell
cd frontend
npm ci
npm run dev
```

Use a Node.js version supported by the installed Vite release (Node 24 was
used for this project). Open `http://localhost:5173`. The React/TypeScript
frontend calls the backend directly at `http://localhost:5080` by default
in development. No backend static-file hosting or proxy is required.
Development allows exactly `http://localhost:5173` and
`http://127.0.0.1:5173` as browser origins unless overridden.

Choose a synthetic evidence collection, enter a question, and click
**Investigate incident**. Fixed retrieval is the default. Optional tools allow
bounded read-only evidence collection. The report includes expandable original
citations, uncertainty, suggested checks marked not executed, and model usage.
Loading, cancellation, insufficient evidence, and API errors are explicit.
Reports are kept only in page memory. Each investigation consumes Azure quota;
loading the UI/catalog does not. See the backend evaluations for known quality
limitations; the UI does not turn citations into proof of correctness.

## Independent builds and deployment configuration

Backend:

```powershell
cd C:\Projects\IIQ\backend
dotnet publish src\IncidentIQ.Api --configuration Release --output .local\publish
```

Frontend:

```powershell
cd C:\Projects\IIQ\frontend
$env:VITE_API_BASE_URL = "https://your-backend-host.example"
npm run build
```

Deploy `frontend\dist` to a static frontend host and the backend publish output
to an ASP.NET Core host. The frontend does not bundle or require backend files.
`VITE_API_BASE_URL` is a public, build-time backend origin, not a secret.
Rebuild the frontend if it changes. Production builds do not silently fall
back to localhost: missing configuration is displayed as a connection error.
Never put Azure API keys in any `VITE_` variable.

Set the backend environment variable `Cors__AllowedOrigins__0` to the exact
frontend origin, for example `https://your-frontend-host.example` (no trailing
slash). Additional origins use `__1`, `__2`, and so on. Production allows no
cross-origin browser requests unless configured. CORS controls browsers, not
API authentication or spending: this demo has no authentication, rate limiting,
or spending cap. Do not expose it publicly with unrestricted paid inference.

Frontend validation commands:

```powershell
npm run lint
npm run build
```

Backend evaluations remain separate:

```powershell
cd C:\Projects\IIQ\backend
.\scripts\Invoke-IncidentEvaluation.ps1
```

Unit-test projects and Docker remain deferred.
