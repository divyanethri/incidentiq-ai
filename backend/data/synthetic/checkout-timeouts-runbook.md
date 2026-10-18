# Checkout timeout investigation runbook

SYNTHETIC DATA ONLY. This document describes a fictional checkout service.
Service: checkout-api
Purpose: investigate elevated checkout latency and HTTP 504 responses.

## Collect evidence before choosing a cause
Record the incident time window, affected service version, and deployment time.
Compare request latency and errors before and after the deployment.
Check both application resource limits and downstream dependency health.
A previous incident is a useful hypothesis, not proof of the current cause.

## Connection pool investigation
Find the database pool maximum, active connections, and waiting requests.
Look for database.connection.acquire.timeout events near failing requests.
If active connections equal the maximum and waiting requests rise, investigate pool saturation.
Compare the configured pool maximum with the previous deployment.
A healthy database does not rule out an application-side connection bottleneck.

## Alternative explanations
Payment failures can also cause checkout timeouts; inspect payment dependency events.
Database outages can cause similar symptoms; inspect database health and query latency.
Traffic spikes can exhaust a correctly configured pool; compare request rates.
Missing dependency telemetry means those alternatives remain uncertain.

## Recommended next steps
Have an operator compare configuration changes with the last known good release.
Recommend rollback only when deployment timing and supporting evidence justify it.
After any operator action, compare errors, latency, and connection waiting counts.
Never treat a rollback recommendation as permission for an AI tool to execute it.
Record the source file and line range for each conclusion.
