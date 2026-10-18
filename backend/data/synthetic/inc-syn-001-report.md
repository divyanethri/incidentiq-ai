# Previous incident: INC-SYN-001

SYNTHETIC DATA ONLY. This is a fictional historical incident, not the current incident.
Service: checkout-api
Date: 2026-10-10
Window: 09:00 to 09:20 UTC
Severity: simulated SEV2

## Symptoms
Checkout returned HTTP 504 responses shortly after release 1.6.0.
Incoming request rate was similar to the pre-deployment baseline.
Requests waited for database connections even though database queries were fast.

## Evidence recorded during the historical investigation
Release 1.5.9 used a maximum database pool size of 80 connections.
Release 1.6.0 accidentally reduced the pool maximum to 4.
Active connections reached 4 while connection waiting counts increased.
Database health checks and payment requests continued to succeed.

## Confirmed historical cause and resolution
A deployment configuration override reduced application connection capacity.
An operator rolled back to release 1.5.9 at 09:15 UTC.
Error rate and connection waiting counts returned to baseline by 09:20 UTC.
The team added a configuration review step before deployment.

## Limits of this evidence
This report supports checking pool configuration in similar incidents.
It does not establish the cause of INC-SYN-002.
The current deployment, log events, and alternative explanations must be checked separately.
