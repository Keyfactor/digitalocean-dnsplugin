## Overview

The DigitalOcean Provider plugin enables automated DNS-based domain validation for Keyfactor certificate lifecycle management through DigitalOcean. This plugin integrates with the DigitalOcean API to automatically create, verify, and delete DNS TXT records required for domain validation during certificate issuance and renewal.

## Features

- Bearer token authentication using a DigitalOcean Personal Access Token
- Automatic zone discovery across all domains on the account, matched by longest domain suffix

## Requirements

- A DigitalOcean account with one or more domains managed by DigitalOcean's DNS
- A DigitalOcean Personal Access Token with `domain:read`, `domain:create`, and `domain:delete` scopes (create under **API > Tokens** in the DigitalOcean control panel)
