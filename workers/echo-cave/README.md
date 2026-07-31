# BohemiX Echo Cave Worker

This Cloudflare Worker receives Echo Cave messages, checks the caller's current
Afdian plan on the server, and stores accepted messages in Cloudflare D1.

## Deploy

1. Install dependencies and authenticate with the Cloudflare account that will own the endpoint.

   ```powershell
   npm install
   npx wrangler login
   ```

2. Create the D1 database.

   ```powershell
   npx wrangler d1 create bohemix-echo-cave
   ```

3. Copy `wrangler.example.toml` to `wrangler.toml`, then replace its
   `database_id` with the ID returned by the previous command. The example already
   sets the BohemiX Afdian creator ID and the two Echo Cave plans: "熏奶酪" and
   "救世干酒".

4. Create the database table.

   ```powershell
   npx wrangler d1 execute bohemix-echo-cave --remote --file migrations/0001_create_echo_messages.sql
   ```

5. Store the Afdian open API token as a Cloudflare secret. Do not put this value
   in `wrangler.toml`, the desktop app, or source control.

   ```powershell
   npx wrangler secret put AFDIAN_TOKEN
   ```

6. Deploy.

   ```powershell
   npx wrangler deploy
   ```

   Wrangler prints a URL such as `https://bohemix-echo-cave.<account>.workers.dev`.
   Make it available to the desktop app for the current process:

   ```powershell
   $env:BOHEMIX_ECHO_CAVE_ENDPOINT = "https://bohemix-echo-cave.<account>.workers.dev"
   ```

The app reads that environment variable through
`feedbackEndpointEnvironmentVariable` in `community-hub.json`. A release launcher
or deployment environment should set it before starting BohemiX. Public desktop
builds do not need `BOHEMIX_AFDIAN_TOKEN`: their plan verification is routed to the
Worker's protected `/verify` endpoint.

## Operations

View stored messages with:

```powershell
npx wrangler d1 execute bohemix-echo-cave --remote --command "SELECT id, created_at, supporter_name, plan_name, subject, message, contact FROM echo_messages ORDER BY id DESC LIMIT 100"
```

The Afdian open API verifies that an identifier belongs to a current eligible
subscription, but it does not authenticate the person operating the desktop app.
For stricter identity assurance, add an account-linking or one-time-code flow
before treating a submitted identifier as a person identity.
