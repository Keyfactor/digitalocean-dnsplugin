<h1 align="center" style="border-bottom: none">
    DigitalOcean DNS Provider
</h1>

<p align="center">
  <!-- Badges -->
<img src="https://img.shields.io/badge/integration_status-production-3D1973?style=flat-square" alt="Integration Status: production" />
<a href="https://github.com/Keyfactor/digitalocean-dnsplugin/actions/workflows/keyfactor-starter-workflow.yml"><img src="https://github.com/Keyfactor/digitalocean-dnsplugin/actions/workflows/keyfactor-starter-workflow.yml/badge.svg" alt="Build" /></a>
<a href="https://github.com/Keyfactor/digitalocean-dnsplugin/releases"><img src="https://img.shields.io/github/v/release/Keyfactor/digitalocean-dnsplugin?style=flat-square" alt="Release" /></a>
<img src="https://img.shields.io/github/issues/Keyfactor/digitalocean-dnsplugin?style=flat-square" alt="Issues" />
<img src="https://img.shields.io/github/downloads/Keyfactor/digitalocean-dnsplugin/total?style=flat-square&label=downloads&color=28B905" alt="GitHub Downloads (all assets, all releases)" />
</p>

<p align="center">
  <!-- TOC -->
  <a href="#support">
    <b>Support</b>
  </a>
  ·
  <a href="#requirements">
    <b>Requirements</b>
  </a>
  ·
  <a href="#installation">
    <b>Installation</b>
  </a>
  ·
  <a href="#license">
    <b>License</b>
  </a>
  ·
  <a href="https://github.com/orgs/Keyfactor/repositories?q=dnsplugin">
    <b>Related Integrations</b>
  </a>
</p>

## Overview

The DigitalOcean Provider plugin enables automated DNS-based domain validation for Keyfactor certificate lifecycle management through DigitalOcean. This plugin integrates with the DigitalOcean API to automatically create, verify, and delete DNS TXT records required for domain validation during certificate issuance and renewal.

## Features

- Automated DNS TXT record creation and deletion in DigitalOcean
- Bearer token authentication using a DigitalOcean Personal Access Token
- Automatic zone discovery across all domains on the account, matched by longest domain suffix

## Requirements

### Keyfactor Platform
- Keyfactor AnyCA Gateway REST **26.2 or later** (DNS validation support was added in AnyCA Gateway 26.2)
- A gateway product that supports DNS-01 domain validation (ACME REST Gateway, DigiCert, Sectigo, etc.)

### DigitalOcean Requirements

- A DigitalOcean account with one or more domains managed by DigitalOcean's DNS
- A DigitalOcean Personal Access Token with `domain:read`, `domain:create`, and `domain:delete` scopes (create under **API > Tokens** in the DigitalOcean control panel)

### Runtime Requirements
- .NET 10.0 runtime (provided by the gateway server)
- Network connectivity to api.digitalocean.com (HTTPS/443)

## Installation

This plugin is installed alongside any Keyfactor gateway server that supports DNS-01 domain validation (ACME REST Gateway, DigiCert, Sectigo, etc.). The same DLL works with every supported gateway.

> See the official Keyfactor AnyCA Gateway REST installation documentation for the authoritative install instructions: **<TBD link from Sarah Duncan>**. The steps below are a general guide; defer to the official docs if they diverge.

### 1. Download the Plugin

Download the latest release from the [Releases](https://github.com/Keyfactor/digitalocean-dnsplugin/releases) page.

### 2. Copy the plugin DLLs to the gateway's Extensions folder

On the server hosting your gateway, unzip the release and copy the contents of the `net10.0` directory into the gateway's `Extensions` folder.

**Windows** (example path — substitute the gateway product folder for your install):

```text
C:\Program Files\Keyfactor\<GatewayName>\AnyGatewayREST\net10.0\Extensions\
```

**Linux**:

```text
/opt/keyfactor/<gateway-name>/AnyGatewayREST/net10.0/Extensions/
```

Replace `<GatewayName>` (or `<gateway-name>` on Linux) with the gateway you are installing into (e.g. `AcmeGwDns`, `DigiCert`, `Sectigo`).

### 3. Restart the gateway service

Restart the AnyGatewayREST Windows service for the gateway you installed the plugin into so the Extensions folder is rescanned.

## Configuration

After installing the plugin DLL into the gateway's Extensions folder, configure a new DNS Provider entry in the AnyCA Gateway REST UI and select **DigitalOcean** as the provider type. See the official Keyfactor AnyCA Gateway REST documentation for the canonical UI walkthrough: **<TBD link from Sarah Duncan>**.

### DigitalOcean Setup

Create a DigitalOcean Personal Access Token for the account that owns the domains you want the plugin to manage:

1. Log in to the DigitalOcean control panel
2. Navigate to **API > Tokens**
3. Generate a new token with `domain:read`, `domain:create`, and `domain:delete` scopes and copy the value

Provide the token as `DigitalOcean_ApiToken` in the plugin configuration below.

### Configuration Parameters

| Parameter | Description | Required | Example |
|-----------|-------------|----------|---------|
| `DigitalOcean_ApiToken` | DigitalOcean Personal Access Token with domain read/create/delete scopes. Created under API > Tokens in the DigitalOcean control panel. | Yes | ` ` |

### Example Configuration

**Standard configuration:**

```json
{
  "DigitalOcean_ApiToken": "your-digitalocean-api-token"
}
```

## Usage

### Automatic Domain Validation

Once configured, the plugin automatically handles DNS validation during certificate enrollment and renewal:

1. **Record Creation**: Plugin creates a DNS TXT record with the validation challenge
2. **Propagation Wait**: Plugin waits for DNS propagation
3. **Verification**: Plugin verifies the record exists on DigitalOcean nameservers
4. **Cleanup**: Plugin deletes the validation record after successful validation

### Zone Discovery

The plugin discovers the appropriate DigitalOcean domain for a record by querying the DigitalOcean API for all domains on the account, then matching the record's domain against domain names from most specific (longest) to least specific.

### Testing Connectivity

Test DigitalOcean connectivity using `curl` against the API:

```bash
# List domains accessible to the account (validates the API token)
curl -s -H "Authorization: Bearer $DIGITALOCEAN_API_TOKEN" https://api.digitalocean.com/v2/domains
```

## Troubleshooting

### Common Issues

**Authentication Failures**

Symptom: `401 Unauthorized` listing domains

- Verify the API token has not expired or been revoked in the DigitalOcean control panel
- Confirm the token has `domain:read`, `domain:create`, and `domain:delete` scopes (or is a full-access token)

**Zone Not Found**

Symptom: `No DigitalOcean domain found for example.com`

- Verify the domain exists and is active in the DigitalOcean account
- Confirm the account associated with the API token owns that domain

### Logging

Enable debug logging in the gateway's logging configuration:

```json
{
  "Logging": {
    "LogLevel": {
      "Keyfactor.Extensions.DomainValidator.DigitalOcean": "Debug"
    }
  }
}
```

### Service Status

Check DigitalOcean service status: https://status.digitalocean.com/

## Support

The DigitalOcean DNS Provider plugin is supported by Keyfactor for Keyfactor customers. If you have a support issue, please open a support ticket via the Keyfactor Support Portal at https://support.keyfactor.com.

### Resources

- [DigitalOcean Documentation](https://docs.digitalocean.com/reference/api/reference/domains/)
- [Report Issues](https://github.com/Keyfactor/digitalocean-dnsplugin/issues)
- [Discussions](https://github.com/Keyfactor/digitalocean-dnsplugin/discussions)

> To report a problem or suggest a new feature, use the **[Issues](../../issues)** tab. If you want to contribute actual bug fixes or proposed enhancements, use the **[Pull requests](../../pulls)** tab.

## License

Apache License 2.0, see [LICENSE](LICENSE).

## Related Integrations

See all [Keyfactor DNS Provider plugins](https://github.com/orgs/Keyfactor/repositories?q=dnsplugin).
