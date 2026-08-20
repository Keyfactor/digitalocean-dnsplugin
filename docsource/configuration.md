### Provider Setup

Create a DigitalOcean Personal Access Token for the account that owns the domains you want the plugin to manage:

1. Log in to the DigitalOcean control panel
2. Navigate to **API > Tokens**
3. Generate a new token with `domain:read`, `domain:create`, and `domain:delete` scopes and copy the value

Provide the token as `DigitalOcean_ApiToken` in the plugin configuration below.

### Example Configurations

**Standard configuration:**

```json
{
  "DigitalOcean_ApiToken": "your-digitalocean-api-token"
}
```

### Zone Discovery

The plugin discovers the appropriate DigitalOcean domain for a record by querying the DigitalOcean API for all domains on the account, then matching the record's domain against domain names from most specific (longest) to least specific.

### Testing Connectivity

Test DigitalOcean connectivity using `curl` against the API:

```bash
# List domains accessible to the account (validates the API token)
curl -s -H "Authorization: Bearer $DIGITALOCEAN_API_TOKEN" https://api.digitalocean.com/v2/domains
```

### Troubleshooting

**Authentication Failures**

Symptom: `401 Unauthorized` listing domains

- Verify the API token has not expired or been revoked in the DigitalOcean control panel
- Confirm the token has `domain:read`, `domain:create`, and `domain:delete` scopes (or is a full-access token)

**Zone Not Found**

Symptom: `No DigitalOcean domain found for example.com`

- Verify the domain exists and is active in the DigitalOcean account
- Confirm the account associated with the API token owns that domain
