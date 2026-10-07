# CloudFlare DNS Updater Service

This service updates all `A` records in all `zones` to the actual external ip.

# Build

```bash
docker-compose -f docker-compose.yml build
```

# Configure

Add your credentials to `docker-compose.yml`.
It is enough to fill `Email` and `ApiKey` **or just** `ApiToken`

```yaml
version: '3.4'

services:
  cloudflarednsupdater:
    image: cloudflarednsupdater
    container_name: cloudflarednsupdater
    build:
      context: .
      dockerfile: CloudFlareDnsUpdater/Dockerfile
    environment:
      - ASPNETCORE_ENVIRONMENT=Production
      - CloudFlare__Email=email@example.com
      - CloudFlare__ApiKey=yourApiKey
      # or
      - CloudFlare__ApiToken=yourApiToken
      # optional
      - UpdateIntervalSeconds=30
      - LimitToZoneByDomain=example.com
      - ExcludeRecords=calitally.example.com,nas.example.com
      - SkipPrivateIpRecords=true
    restart: unless-stopped
```

## Settings

| Setting | Default | Meaning |
|---|---|---|
| `UpdateIntervalSeconds` | `30` | How often the external IP is checked |
| `LimitToZoneByDomain` | *(empty)* | Only touch zones and records whose name ends with this domain |
| `ExcludeRecords` | *(empty)* | Comma-separated record names that are never updated, for `A` records that deliberately point somewhere else (a LAN-only host behind a split-horizon name, a record for another server). Matching is case-insensitive and ignores a trailing dot |
| `SkipPrivateIpRecords` | `true` | Leave `A` records alone when their current address is private (RFC 1918 `10/8`, `172.16/12`, `192.168/16`, loopback, link-local or CGNAT `100.64/10`). Such records cannot be meant to track the external IP. Set to `false` to restore the old behaviour of updating every `A` record |

Every setting can also be given in `appsettings.json` or on the command line (`--ExcludeRecords calitally.example.com`).

# Run

```bash
docker-compose -f docker-compose.yml up -d
```

# Example output
![Output](https://github.com/zingz0r/CloudFlareDnsUpdater/blob/master/output.png)
