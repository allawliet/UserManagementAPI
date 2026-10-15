# UserManagementAPI

User Management API for TechHive Solutions (HR and IT). ASP.NET Core minimal API on .NET 10 with an in-memory store.

## Run

```bash
dotnet run --project UserManagementAPI --urls http://localhost:5080
```

All `/api/*` routes need a token: `Authorization: Bearer <token>`. In Development the token is `dev-only-token-change-me` (from `appsettings.Development.json`). The status page at `/` is public. In Development, the OpenAPI document is served at `/openapi/v1.json` (summaries, real status codes and the bearer scheme are documented). Requests for manual testing are in [UserManagementAPI.http](UserManagementAPI/UserManagementAPI.http); the same calls work in Postman.

## Authentication

Tokens come from configuration (`Auth:Tokens`), never from code. For anything beyond local development set your own, for example:

```bash
export Auth__Tokens__0="a-long-random-value"
export Auth__Tokens__1="a-second-token-for-another-client"   # optional; allows rotation
```

If no token is configured the API rejects every protected request (it fails closed) and logs a warning at startup. Tokens are compared in constant time and are never logged. Leading and trailing spaces in configured tokens are ignored.

Deployment: send tokens only over HTTPS and have clients call the `https://` URL directly. The HTTPS redirect used outside Development cannot protect a token that was already sent over plain HTTP, because the `Authorization` header reaches the server before the redirect.

## Endpoints

| Method | Route | Success | Errors |
|--------|-------|---------|--------|
| GET | `/api/users?page=1&pageSize=20` | 200 list of users, total in `X-Total-Count` header (pageSize max 100) | 400 bad paging |
| GET | `/api/users/{id}` | 200 user | 404 |
| POST | `/api/users` | 201 + `Location` header | 400 validation or bad body, 409 duplicate email, 413 body over 16 KB |
| PUT | `/api/users/{id}` | 200 updated user | 400, 404, 409 (the body is validated first, so an invalid body gets 400 even for an unknown id) |
| DELETE | `/api/users/{id}` | 204 | 404 |

Request body for POST and PUT (all fields required):

```json
{ "firstName": "Alan", "lastName": "Turing", "email": "alan@techhive.example", "department": "IT" }
```

## Structure

- `UserManagementAPI/Program.cs`: service registration, validation helper, endpoint mapping
- `UserManagementAPI/Models/User.cs`: `User` record and the `UserRequest` DTO with validation attributes
- `UserManagementAPI/Services/UserStore.cs`: in-memory store behind a single lock, with an email index; seeded with two users
- `UserManagementAPI/Middleware/`: `ErrorHandlingMiddleware`, `TokenAuthenticationMiddleware`, `RequestLoggingMiddleware`, plus `ErrorResponse` (shared JSON error body) and `LogSanitizer`
- `UserManagementAPI.Tests`: xunit integration tests (`dotnet test` from the repository root)

## Test results

Activity 1, run against the live API with curl:

| Case | Result |
|------|--------|
| GET all / GET by id | 200 |
| GET unknown id | 404 |
| POST valid | 201, id assigned |
| POST empty/invalid fields | 400 with per-field errors |
| POST duplicate email (case-insensitive) | 409 |
| PUT existing | 200, `createdAt` preserved |
| PUT unknown id | 404 |
| PUT email belonging to another user | 409 |
| DELETE existing / DELETE again | 204 / 404 |

## Debugging summary (activity 2)

Each bug below was reproduced with curl against the activity 1 code before being fixed. All fixes are covered by `UserManagementAPI.Tests` (activity 2 tests).

| # | Bug (reproduced) | Fix |
|---|------------------|-----|
| 1 | Duplicate emails: 3 of 20 simultaneous POSTs with the same email all returned 201. The "email in use" check and the insert were separate steps. | Check and insert now happen atomically under one lock in `UserStore`. 20 simultaneous POSTs now give one 201 and nineteen 409. |
| 2 | PUT could return `200 null` if the user was deleted between the lookup and the update. | `Update` does lookup, duplicate check and write in one locked step and returns `NotFound`, `DuplicateEmail` or `Ok`. |
| 3 | Weak email validation: `a@b` was accepted. | New `StrictEmail` attribute requires a parseable address with a dotted domain and no display name. |
| 4 | Whitespace was validated before trimming, so `" Alan "` near the length limit was rejected while the stored value was trimmed afterwards. | Input is trimmed first, then validated, then stored. |
| 5 | Empty, `null`, malformed or wrong-typed bodies returned 400 but with a full stack trace page in Development and an inconsistent format. | Uniform `application/problem+json` with no internals leaked (now done by `ErrorHandlingMiddleware`, activity 3). |
| 6 | No global handling of unexpected exceptions (any bug would surface as a bare 500 or a stack trace). | Same handler returns a generic 500 problem document and logs the real exception. Covered by a test that forces a throw. |
| 7 | Validation error keys were PascalCase (`FirstName`) while the JSON body uses camelCase. | Error keys are camelCase (`firstName`). |
| 8 | `GET /api/users` returned every user in one response (758 KB at 6,000 users) and sorted on every call; the email check scanned all users. | Paging (default 20, max 100, `X-Total-Count` header), ordered storage, and an email index for constant-time lookup. At 6,000 users the old code was still fast (a few ms), so this is a scalability fix, not a measured speedup. |
| 9 | No cap on request body size. | Kestrel limit of 16 KB; larger bodies get 413. |
| 10 | Found by Copilot review: `?page=2147483647` overflowed the offset calculation and returned page 1 instead of an empty list. | Offset computed as `long`; offsets past the end return an empty list. |
| 11 | Found by Copilot review: the store accepted `GetPage(0, 20)` (returned page 1) and untrimmed or blank fields, so `" a@b.co "` bypassed the duplicate check. Only reachable by code that skips validation. | `UserStore` rejects page arguments below 1 and non-trimmed or blank fields. |
| 12 | Found by Copilot review: the email check accepted `a@-x.com`, `a.@x.com`, 65-character local parts, address literals, quoted local parts, `a@b.c`, and (found while probing) a trailing zero-width space. | Explicit email policy in `StrictEmailAttribute` (printable ASCII, DNS-style labels, length limits, real TLD). |
| 13 | Found by Copilot review: an exception after the response had started was logged as `500` although the client had received another status. | The committed status is logged, at Error level, with an "aborted" note. The sanitizer also covers Unicode separators and format characters. |
| 14 | Found by Copilot review: the public exemption for `/` and `/openapi` applied to every HTTP method (and `/openapi` to every environment), and a configured token with surrounding spaces could never match the trimmed header. | Only `GET`/`HEAD` are public, `/openapi` only in Development; configured tokens are trimmed before hashing. |

Behavior changes to be aware of: the list endpoint now returns 20 users by default, and 404/409 responses now have a JSON problem body instead of an empty body or `{error}`.

## Middleware (activity 3)

Pipeline order in `Program.cs`:

```
ErrorHandlingMiddleware  ->  (HTTPS redirect, non-Development)  ->  TokenAuthenticationMiddleware  ->  RequestLoggingMiddleware  ->  endpoints
```

| Middleware | Behavior |
|------------|----------|
| `ErrorHandlingMiddleware` | Outermost, so it catches exceptions from everything below it. Unexpected exceptions become `500` with `{"title":"Internal server error.","status":500,"error":"Internal server error.","traceId":"..."}`; the real exception is logged but never sent to the client. Malformed bodies become 400 and oversized bodies 413; other `BadHttpRequestException` statuses (e.g. 408) keep their status with a matching generic message. |
| `TokenAuthenticationMiddleware` | Requires `Authorization: Bearer <token>` on everything except `GET`/`HEAD` of `/` (and of `/openapi` in Development only). Missing, malformed or unknown tokens get `401` with a `WWW-Authenticate: Bearer` header and the same JSON error shape. Runs before routing, so unknown routes also return 401 and an anonymous caller cannot probe which routes exist. |
| `RequestLoggingMiddleware` | Logs `HTTP {Method} {Path} responded {Status} in {ms} ms` for every request that reaches it: Information for 2xx/3xx, Warning for 4xx, Error for 5xx (including exceptions, whose status is logged before the error middleware turns them into a response). Control characters, line/paragraph separators and Unicode format characters are replaced in the method and path. If an exception happens after the response has started, the committed status is logged at Error level with an "aborted" note. No headers, query strings or bodies are logged. |

All error responses (400, 401, 404, 409, 413, 500) share one JSON shape: standard problem details (`title`, `status`, `traceId`) plus an `error` field with the human-readable message.

**Design note on the required order.** The assignment puts logging last. A consequence is that requests rejected by authentication never reach the logging middleware, so the authentication middleware writes its own audit line for them (`HTTP GET /api/users responded 401 (invalid token)`). If you would rather have one log format for everything, move `RequestLoggingMiddleware` between the error and authentication middleware; it will then also log 401s, at the cost of logging unauthenticated traffic.

### Middleware test results

`dotnet test` from the repository root: 132 tests, all passing (34 API tests, 11 store tests, 32 email validation tests, 31 for middleware and the log sanitizer, 3 logging edge cases, 7 error message tests, 8 authentication hardening tests, 6 OpenAPI document tests). The middleware tests cover:

| Area | Cases |
|------|-------|
| Authentication | valid token; missing token; wrong, truncated and wrong-case tokens; `Basic` scheme; empty bearer; every route and verb returns 401 without a token; unknown route returns 401; `/` stays public; no tokens configured fails closed |
| Error handling | forced exception returns 500 JSON with no message or stack trace; auth runs before the endpoint; 400, 401, 404, 409 and 500 all share one JSON shape |
| Logging | method, path and status logged for 200, 404 and 400; 500 logged at Error level along with the exception; rejected requests are audited; tokens never appear in logs; no multi-line log entries; sanitizer unit tests |

Also checked by hand against a running server: log lines on the console for valid, invalid and missing tokens, 404, 400 and the public page.

## Copilot review log

The code was first drafted with an AI coding assistant. I then used GitHub Copilot Chat to review it, record issues and decide what to change. Each entry below is a Copilot session on the finished code, what it found, and what I did about it.

### Review 1: project setup (`Program.cs`)

**Prompt:** "Explain how this minimal API is set up and suggest improvements."

**What Copilot did:** read `Program.cs`, the middleware, the store and the DTOs, then explained the startup path: service registration, the middleware order (error handling, HTTPS redirect outside Development, token authentication, request logging, status-code pages), the `/api/users` route group, DTO validation and the locked in-memory store. It then suggested four improvements.

| Copilot suggestion | Verified? | Action taken |
|--------------------|-----------|--------------|
| `page` is unbounded, so `(page - 1) * pageSize` can overflow in `UserStore.cs` | Yes, but the symptom differed from Copilot's guess of a 500: the offset wraps around and `?page=2147483647` returned the first page of users instead of an empty list | **Fixed.** The offset is now computed as a `long`, and offsets past the end return an empty list. Two regression tests added (`Huge_page_number_returns_empty_list_instead_of_wrapping_to_page_one`) |
| Use durable storage (EF Core and a database) for real deployments | Agreed | Not changed. The in-memory store is intentional for this project; listed under further work |
| Replace the shared-token gate with ASP.NET Core authentication and authorization when roles, expiry or per-client rules are needed; consider whether `/openapi` should be public in production | Agreed on the first part. The `/openapi` exemption is harmless today because the document is only mapped in Development | Not changed. Shared tokens meet the assignment; noted under further work |
| Consider logging before authentication so rejected requests use the standard log format | Agreed it is a trade-off | Not changed. The assignment specifies authentication before logging, and the auth middleware already audits its 401s. The alternative is described under "Design note on the required order" above |

**Result:** one real bug found and fixed. After the fix `dotnet test` reported 58 passing tests.

### Review 2: CRUD endpoints (`Program.cs`)

**Prompt:** review the route handlers against HTTP semantics and look for edge cases in binding, validation, paging and response bodies.

**What Copilot did:** read the handlers and checked their status codes. It could not find the test project (its file search returned no matches), so its notes on "already covered" behavior were not based on the tests. It reported one finding and two behaviors to make explicit.

| Copilot point | Verified? | Action taken |
|---------------|-----------|--------------|
| Large `page` values can overflow the offset and return the first page instead of the requested one | Yes, and this time Copilot described the symptom correctly (wrap to page 1, not a 500). It is the same bug as Review 1. Its line references (`UserStore.cs:23`) point at the code before the fix; the current offset code is on line 26 | Already fixed in Review 1, no new change |
| Status codes are conventional: 200 read/update, 201 + `Location` on create, 204 on delete, 404 missing, 409 duplicate email, 400 invalid input | Yes, all covered by `Crud_round_trip` and the 404, 409 and 400 tests | None needed |
| `PUT` validates the body before checking that the ID exists, so a missing ID with an invalid body gives 400 and with a valid body gives 404 | Yes, confirmed in `Program.cs` (validation runs before `store.Update`) | Kept as is. Both orders are defensible and the 400 never leaks whether the user exists. Added `Put_validates_the_body_before_looking_up_the_id` so the behavior is explicit, and noted it in the endpoints table |
| Auth runs before routing, so unauthenticated requests to unknown routes get 401; authenticated ones should get 404 | Yes. The 401 half was already tested; the 404 half was not | Added `Authenticated_request_to_unknown_route_returns_404_json`, which also checks the JSON error body |

**Result:** no new bug (the one real finding was the Review 1 fix). Two behaviors that were true but untested now have tests. `dotnet test` reported 60 passing tests at that point.

### Review 3: store concurrency (`Services/UserStore.cs`)

**Prompt:** review the store's locking, indexes, pagination and its callers' validation assumptions, and decide whether each operation is safe on its own and whether the store enforces the invariants its data structures depend on.

**What Copilot did:** searched for every use of `UserStore`, read the store and its callers in `Program.cs`, and reported two lower-priority (P2) findings and a concurrency verdict. Its line references (`UserStore.cs:32`, `:44`) do not match the file; the methods are on different lines.

| Copilot point | Verified? | Action taken |
|---------------|-----------|--------------|
| `GetPage` accepts `page` or `pageSize` below 1, e.g. `GetPage(0, 20)` returns the first page | Yes. I called the store directly: `GetPage(0,20)` and `GetPage(-1,20)` returned page 1, `GetPage(1,0)` and `GetPage(1,-5)` returned nothing. Not reachable over HTTP because the endpoint validates first | **Fixed.** `GetPage` throws `ArgumentOutOfRangeException` for values below 1. Tests added |
| `Add`/`Update` rely on callers to validate and trim, and use `!` on the fields | Yes, with a correction to Copilot's wording: a null email throws `ArgumentNullException`, but a null first name was stored, and `" sp@x.co "` was stored untrimmed, so a later `sp@x.co` was accepted as not a duplicate. Not reachable over HTTP because `Normalize()` and `Validate()` run first | **Fixed.** `Add` and `Update` reject any field that is null, blank or untrimmed with `ArgumentException`, so the email index cannot be corrupted by a caller that skips validation. Tests added |
| No race in the current operations: reads and writes share one lock, so the email check and the insert/update are atomic | Agrees with my own check: 20 simultaneous POSTs with the same email give one 201 and nineteen 409 (activity 2, bug 1), and `UserApiTests` still covers it | None needed |

**Result:** two defensive fixes, no HTTP-visible bug. The new guards fail loudly instead of silently returning the wrong page or corrupting the index. `dotnet test` reports 71 passing tests (34 API, 11 store, 26 middleware).

### Review 4: validation (`Models/User.cs`)

**Prompt:** review the email validator and the normalization and length rules around it, and suggest boundary inputs worth testing.

**What Copilot did:** read the validator and confirmed `a@b` and display-name forms are already rejected. It then listed four groups of inputs to probe: malformed domain labels, malformed local-part dots, per-part length limits, and policy-dependent forms (address literals, quoted local parts, single-character suffixes). It said the results depend on the .NET `MailAddress` parser and should be confirmed by test, and that syntax checks cannot prove an address can receive mail.

I ran about 35 inputs through the validator before changing anything:

| Input group | Result before the fix |
|-------------|-----------------------|
| `a@-example.com`, `a@example-.com`, `a@exam_ple.com` | accepted (bad domain labels) |
| `a.@example.com` | accepted (trailing dot in local part). `.a@…` and `a..b@…` were already rejected |
| 65-character local part, 64-character domain label | accepted (over the DNS and SMTP limits) |
| `a@[127.0.0.1]`, `"first last"@example.com` | accepted (address literal, quoted local part) |
| `a@b.c`, `a@example.123`, `a@1.2.3.4` | accepted (one-letter or numeric TLD) |
| `a@sub..example.com` | already rejected |
| `a@example.co` followed by a zero-width space | accepted. **Copilot did not list this one**; an invisible character can make two visually identical emails distinct |
| `ünï@example.com`, `a@münchen.de` | accepted (non-ASCII) |

**Action taken:** all of Copilot's probes were real gaps, so I replaced the loose check with an explicit policy in `StrictEmailAttribute`: printable ASCII only (this also blocks invisible characters), no quotes or address literals, local part 1–64 characters with no leading or trailing dot, domain labels 1–63 characters of letters, digits and inner hyphens, at least two labels, and a TLD of at least two characters that is not all digits. Ordinary addresses such as `a+tag@example.com`, `o'brien@example.com` and punycode domains (`a@xn--mnchen-3ya.de`) still pass. 32 new tests in `EmailValidationTests` cover the accepted and rejected forms.

**Trade-off:** rejecting non-ASCII addresses is a policy choice that suits an internal HR/IT directory. If TechHive needs internationalized email addresses, that rule is the one to relax. As Copilot noted, syntax validation does not show that a mailbox exists.

**Result:** the validator now enforces a stated policy instead of whatever `MailAddress` allows. `dotnet test` reports 103 passing tests (34 API, 11 store, 32 email validation, 26 middleware).

### Review 5: logging middleware (`RequestLoggingMiddleware.cs`, `LogSanitizer.cs`)

**Prompt:** trace the logger and the sanitizer together and check whether thrown requests get a useful status, whether sensitive request data can enter the log fields, and whether control characters survive sanitization.

**What Copilot did:** read both classes plus the error and authentication middleware call sites. It reported two findings and several observations.

| Copilot point | Verified? | Action taken |
|---------------|-----------|--------------|
| **P2:** if an endpoint starts a response and then throws, the request is logged as `500` even though the client already received the committed status (e.g. `200`) | Yes. I built a test host with the real error and logging middleware and a handler that sends headers and then throws. The log said `responded 500` | **Fixed.** In that case the middleware logs the committed status, at Error level, with the note "(aborted after the response started)". The error middleware still logs the exception. Normal 500s are unchanged. Not reachable through the current routes, which return results without streaming |
| **P3:** `char.IsControl` misses U+2028/U+2029 and bidi or other format characters | Yes for the sanitizer in isolation. With the real Kestrel server these characters are not reachable through the path: I sent `%E2%80%A8`, `%E2%80%AE`, `%E2%80%8B` and raw UTF-8 bytes, and Kestrel logged them still percent-encoded | **Hardened anyway** (defense in depth, in case the host or proxy decodes paths): the sanitizer also replaces line and paragraph separators and Unicode format characters. Non-ASCII letters such as `ü` are kept. Tests added |
| The method is not sanitized | True, but valid HTTP method tokens cannot contain CR/LF | Sanitized too, at no cost |
| Query strings, headers and bodies are never logged; the path is logged as is, so avoid secrets in path segments | Agreed, matches the design and the existing "tokens never appear in logs" test | None needed |
| Auth failures and HTTPS redirects are not in the request log | Known, a consequence of the required order | Already documented under "Design note on the required order"; the auth middleware writes its own 401 line |
| 499 for client cancellations is a logging convention, not a response sent | Agreed | None needed |

**Result:** one defensive fix with a reproducing test, one hardening, no change reachable by an HTTP client. `dotnet test` reports 111 passing tests (34 API, 11 store, 32 email validation, 31 middleware including sanitizer cases, 3 logging edge cases).

### Review 6: error-handling middleware (`ErrorHandlingMiddleware.cs`, `ErrorResponse.cs`)

**Prompt:** inspect the exception handler, the shared response writer and the pipeline order, and check whether headers and content type stay consistent and where exceptions can bypass the JSON error body.

**What Copilot did:** read the handler, the writer and the call sites, then reported two P2 findings and a list of checks. Its line references (`:22`, `:34`) do not match the file.

| Copilot point | Verified? | Action taken |
|---------------|-----------|--------------|
| **P2:** every `BadHttpRequestException` except 413 gets "The request body is missing or malformed", which misdescribes statuses such as 414 and 431 | Yes for the wording: a handler that throws with status 408, 414 or 431 returned the body message. In practice only 400, 413 and possibly 408 can reach this handler: Kestrel rejects over-long request targets (414) and oversized headers (431) before any middleware runs. I also checked the case I expected to be affected, a wrong content type: that is a plain 415 from the minimal API, filled in by the status-code pages as `"Unsupported Media Type"`, so it was already fine | **Fixed.** The status is mapped to a matching generic message (400 body, 408 timeout, 413 body size, 414 target, 431 headers) with a neutral fallback, "The request could not be processed." The exception text is still never sent. 7 tests added |
| **P2:** an exception after the response has started cannot become JSON, and the logger may record 500 although another status was sent | The first half is correct and intended: the handler aborts the connection instead of corrupting the response. The second half is the Review 5 finding, which is already fixed: the logger now records the committed status with an "aborted" note | No new change |
| Before the response starts, the handler clears it and writes `application/problem+json` with `title`, `error`, `status` and `traceId`, and no exception message or stack trace | Yes, covered by the 500 test (`Unhandled_exception_returns_consistent_500_json_without_leaking_details`) and the shared-shape test | None needed |
| Client-aborted cancellations are swallowed; other cancellation exceptions become 500 | Yes, matches the code | None needed |
| The error handler is outermost, so it covers HTTPS redirection, authentication, logging, status-code pages and endpoints. The auth middleware keeps its own `WWW-Authenticate` header because the writer does not clear headers | Yes | None needed |
| Full exceptions are logged server-side for 5xx; messages should not contain secrets | Agreed | Nothing in this project puts secrets into exception messages |

**Result:** one wording fix, no change in behavior for any request the current routes can produce. `dotnet test` reports 118 passing tests (34 API, 11 store, 32 email validation, 31 middleware and sanitizer, 3 logging edge cases, 7 error messages).

### Review 7: authentication and pipeline order (`TokenAuthenticationMiddleware.cs`)

**Prompt:** verify the token parser and the route exemption against the configured tokens and the pipeline order, and check the response helper and the auth-failure log call so the review covers the whole 401 path.

**What Copilot did:** read the middleware, the response helper and the call sites, then reported two P3 findings and a list of checks. Its line references (`:64`, `:22`, `:70`) do not match the file.

| Copilot point | Verified? | Action taken |
|---------------|-----------|--------------|
| **P3:** the `/openapi` exemption (`StartsWithSegments`) applies to every HTTP method and every environment, so a future route under that prefix would bypass authentication | Yes, and it was wider than described: the exemption for `/` was also method-independent. Before the fix `POST /openapi/v1.json`, `DELETE /openapi/v1.json` and `POST /` all skipped authentication, and in Production `/openapi/v1.json` skipped it too (it answered 404, not 401). None of these exposed data, because no such endpoints exist | **Fixed.** Only `GET` and `HEAD` are public. `/` is public in every environment; `/openapi` is public only in Development, where the document is mapped. 6 tests added |
| **P3:** configured tokens are hashed as-is while presented tokens are trimmed, so a token configured with leading or trailing spaces can never match | Yes. With `Auth:Tokens` set to `"  spaced-token  "`, the header `Bearer spaced-token` got 401 | **Fixed** by trimming configured tokens before hashing, the same normalization as the header (this is easy to hit with environment variables). A whitespace-only token is still ignored, so an empty configuration still fails closed. 2 tests added |
| Scheme matching is case-insensitive, missing or empty tokens are rejected, and the token is trimmed. The RFC token character set is not enforced, but the parsed value still has to match a configured token | Yes | None needed |
| The comparison checks every stored hash with `FixedTimeEquals` without stopping at the first match, and no configured tokens means every protected request fails closed | Yes, covered by the no-token and wrong-token tests | None needed |
| A failed check logs the method, sanitized path and a reason, never the token; the 401 uses the shared JSON shape and `WWW-Authenticate: Bearer`. Secrets placed in the path could be logged | Yes. The path is the only request data in the line | None needed. Do not put secrets in URL paths |
| Pipeline order is as required: error handling, HTTPS redirect, authentication, request logging, status-code pages. Auth failures get the auth middleware's own warning instead of a request-log line | Yes | None needed; see "Design note on the required order" |
| **Deployment caveat:** HTTPS redirection does not protect a token a client already sent over plain HTTP | Agreed. A redirect happens after the request, including its `Authorization` header, has reached the server | Documented under Authentication: production clients must call the HTTPS URL directly |

**Result:** two hardening fixes with reproducing tests (5 of the 8 new cases failed before the change), one documented caveat. `dotnet test` reports 126 passing tests (34 API, 11 store, 32 email validation, 31 middleware and sanitizer, 3 logging edge cases, 7 error messages, 8 authentication hardening).

## Further work

- Durable storage (EF Core and a database) instead of the in-memory store.
- ASP.NET Core authentication and authorization (roles, expiry, per-client rules) instead of shared tokens.
- Decide whether non-ASCII email addresses should be accepted (Review 4).

Ideas for further work: persistence with EF Core, ASP.NET Core authentication and authorization with named clients, rate limiting.
