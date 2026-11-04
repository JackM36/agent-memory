# Deploying brain-server

`brain-server` hosts collections for remote clients: your other machines, CI, or teammates. It is one
process with one SQLite database and an optional set of read-only Markdown collections. All of its state
lives in a single data directory.

```text
/data
├─ host.json        transport, listen address, collections, limits
├─ db/brain.db      API keys and SQLite-stored collections (authoritative)
├─ cache/index.db   search index (rebuildable)
├─ proposals/       pending proposals
├─ state/batches/   publish batch journal (resumable batches)
├─ audit/           JSONL audit log of every write
└─ backups/         output of "brain-server backup"
```

Choose one of three ways to run it:

| Setup | When to use it |
|---|---|
| [Docker Compose with Caddy](#docker-compose-with-caddy-recommended) | A VPS such as a DigitalOcean Droplet. Caddy obtains and renews HTTPS certificates. |
| [systemd](#systemd-without-docker) | A Linux host without Docker, behind your own reverse proxy. |
| [Local only](#local-only) | One machine; clients on the same host. |

## Docker Compose with Caddy (recommended)

### 1. Prepare the host

On a fresh Ubuntu Droplet with at least 1 GB of RAM:

```bash
sudo apt-get update && sudo apt-get install -y docker.io docker-compose-v2 git
sudo usermod -aG docker "$USER"   # log out and back in
```

Point a DNS `A` record (and `AAAA` if you use IPv6) for your chosen name, for example
`brain.example.com`, at the Droplet. Allow ports 80 and 443 in the DigitalOcean cloud firewall or `ufw`.
Port 80 is only needed for the certificate challenge and for redirecting to HTTPS.

### 2. Configure

```bash
git clone <this repository> agent-brain && cd agent-brain/deploy
cp .env.example .env
nano .env   # set AGENT_BRAIN_DOMAIN; set AGENT_BRAIN_IMAGE_OWNER to use a published image
```

If `AGENT_BRAIN_IMAGE_OWNER` names a GHCR owner that publishes releases, `docker compose pull` downloads
the image. Otherwise run `docker compose build` and the image is built from this checkout.

### 3. Initialise once

```bash
docker compose run --rm brain-server init --collection personal --title "Personal knowledge"
```

This creates `host.json`, the database, and two keys: an **admin** key and a **read/write** key. Both
are printed once; store them in a password manager. The server keeps only their hashes.

The generated `host.json` uses `transport: behind-proxy`, listens on `0.0.0.0:8080` inside the
container, and trusts forwarded headers only from private address ranges. That covers Caddy on the
Compose network. Requests to `/v1` that did not arrive over HTTPS are refused.

### 4. Start

```bash
docker compose up -d
docker compose ps                       # both services "running", brain-server "healthy"
curl https://brain.example.com/health   # {"status":"ok",...}
```

### 5. Connect a client

On each machine that should use the server, keep the key in an environment variable and register the
connection. It goes into the user configuration, so every project sees it:

```bash
export AGENT_BRAIN_PERSONAL_KEY='brain_…'      # in your shell profile or secret manager
brain connection add personal --url https://brain.example.com --credential-env AGENT_BRAIN_PERSONAL_KEY
brain connection test personal
```

By default no query context leaves the machine. Add `--forward project,technologies` to send the
project id and technologies, which improves ranking of project-scoped entries on the server. Files and
symbols are opt-in in the same way. See [configuration.md](configuration.md) for `sourcePreference`,
timeouts and per-connection collection lists.

### 6. Save to the server

To make the server's collection the default destination for reusable knowledge, set it in your user
configuration (`%APPDATA%\agent-brain\config.json` or `~/.config/agent-brain/config.json`):

```json
{ "schema": 1, "writes": { "knowledge": "personal" } }
```

From then on, `brain new knowledge …` and the agents' `save` with kind `knowledge` write to the server;
project memories stay in each repository. What the key may do decides how a save lands: with
`maintain` it is written, with only `propose` it becomes a proposal, which you review from any machine
with `brain proposals list` and `brain proposals accept personal:p-…` (needs `publish`).

### Adding collections

```bash
docker compose exec brain-server /app/brain-server collections add library.unity --tech unity
docker compose exec brain-server /app/brain-server collections list
docker compose restart brain-server
```

`collections add <id>` adds a SQLite collection to `host.json`. Options: `--storage markdown` (a folder
under `/data`, default `collections/<id>`, created with a `collection.json`), `--path`, `--title`, `--tech`
(default technologies of its entries), `--read-only` and `--immediate-publish`. Keys with
`collection:*:…` scopes reach the new collection at once; others need a key that names it.

One server hosts any number of collections behind one URL. A client connection without a collection
list sees every collection its key can read, so new collections need no client change. Markdown collections are watched and
reindexed when files change, with a full reconciliation every five minutes.

## Keys

```bash
docker compose exec brain-server /app/brain-server keys create --label laptop --scope "collection:*:read" --scope "collection:personal:propose"
docker compose exec brain-server /app/brain-server keys list
docker compose exec brain-server /app/brain-server keys revoke <keyId>
```

| Scope | Allows |
|---|---|
| `collection:<id\|*>:read` | search, list, get, references, backlinks |
| `collection:<id\|*>:propose` | submit proposals; see your own proposals |
| `collection:<id\|*>:maintain` | direct create, update, archive, supersede, resolve, delete |
| `collection:<id\|*>:publish` | publish batches; accept or reject proposals |
| `admin` | reindex, verify; list every proposal |

Give each machine or person its own key, so you can revoke one without touching the others.
Revocation takes effect on the next request.

## Backups

`db/brain.db` is the only file that cannot be rebuilt (with `proposals/` and `audit/` if you want to
keep pending proposals and history). Markdown collections are backed up wherever their
files live, ideally in Git.

```bash
docker compose exec brain-server /app/brain-server backup            # writes /data/backups/brain-<timestamp>.db
docker run --rm -v deploy_brain-data:/data -v "$PWD":/out alpine cp -r /data/backups /out/
```

`backup` uses SQLite's online backup, so it is safe while the server runs. For off-site copies, add a
cron job on the host that runs both commands and ships `backups/` to object storage, such as
DigitalOcean Spaces with `s3cmd` or `rclone`. A Droplet snapshot also captures the volume.

To restore: stop the server, replace `/data/db/brain.db` with the backup, delete `/data/cache/index.db`
(it is rebuilt), and start the server again.

## Upgrades

```bash
cd agent-brain && git pull && cd deploy
docker compose pull      # or: docker compose build
docker compose up -d
```

Database schema upgrades run automatically at start-up. Take a backup first. The server refuses to open
a database written by a newer version.

## systemd (without Docker)

1. Download the `brain-server` archive for your platform from a release, or run
   `dotnet publish src/AgentBrain.Server -c Release -r linux-x64 --self-contained -o /opt/agent-brain`.
2. Create the service user and data directory:
   ```bash
   sudo useradd --system --home /var/lib/agent-brain --shell /usr/sbin/nologin agent-brain
   sudo install -d -o agent-brain -g agent-brain /var/lib/agent-brain
   sudo -u agent-brain /opt/agent-brain/brain-server init --data /var/lib/agent-brain
   ```
3. Install the unit: `sudo cp deploy/systemd/brain-server.service /etc/systemd/system/ && sudo systemctl enable --now brain-server`.
4. Put it behind a TLS reverse proxy (nginx, Caddy) that sets `X-Forwarded-Proto`. Alternatively, set
   `"transport": "tls"` with `"listen": "https://0.0.0.0:443"` and a PKCS#12 certificate in `host.json`:
   ```json
   "certificate": { "path": "/etc/agent-brain/cert.pfx", "passwordEnv": "AGENT_BRAIN_CERT_PASSWORD" }
   ```

## Local only

For a server used only from the same machine:

```json
{ "schema": 1, "transport": "local", "listen": "http://127.0.0.1:7171", "collections": { … } }
```

`local` refuses to start on a non-loopback address. Connect with `--url http://127.0.0.1:7171`.

## Troubleshooting

| Symptom | Cause |
|---|---|
| `403 HTTPS is required` | The proxy is not sending `X-Forwarded-Proto: https`, or its address is outside `trustedProxies`. |
| `401` | Wrong or revoked key, or the client's `credentialEnv` variable is empty. `brain connection test` shows which. |
| `brain doctor` reports a connection as skipped | The environment variable named by `credentialEnv` is not set in that shell. |
| Caddy cannot obtain a certificate | DNS does not point at the host yet, or port 80 is blocked. |
| Markdown edits not visible | Wait for the debounce of about one second; `POST /v1/admin/reindex` forces a refresh. |
