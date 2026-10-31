# API

The contract between the apps and the cloud: an API Gateway HTTP API (the stack's `ApiUrl` output) with one Lambda behind it, `api/tally_api.py`. Keep this file true when either side changes.

## Access

- **Every route needs an access token** for the app's client, from config's `auth` provider or the stack's Cognito user pool (the `Issuer` and `ClientId` outputs). The client is public, uses authorization code with PKCE, and redirects to `tally://callback` and the site's `admin.html`.
- **API Gateway's JWT authorizer** checks the signature, issuer, audience and expiry before the function runs. Without a valid token: 401.
- **Everything is the caller's.** The function takes the token's `sub` as the owner on every request. Meetings live under `users/<sub>/meetings/<id>/`, every table is keyed by `owner` first, and another person's meeting or person is simply not found (404).
- **The function's role** reaches `users/*` and `crashes/*` in the data bucket, the five tables, the crash topic, and, for the dashboard and rewrites, Batch, its own job queue and Cost Explorer. The apps hold no AWS credentials.

## Routes

Bodies and replies are JSON with snake_case keys. Errors are `{"error": "..."}`, with 400 (a bad request), 401, 403 (not an admin, or a bad user id), 404 (not there, or not the caller's), 409 (a conflict: sent already, not ready, changed meanwhile), 429 (too many on their way) or 500.

Every request carries `X-Tally-Version`, and `GET /meetings` records it in the `users` table with the token's name and email. Meeting ids are ULIDs made by the app. A person's name, role and company are one line of at most 100 characters, because they reach the agent's prompt.

| Route | What it does |
|---|---|
| `GET /meetings` | `{meetings: [{meeting_id, state, title, started_at, ended_at, duration, language, error, updated_at, edited_at, rewriting, rewrite_error, people, folder_ids}]}`, newest first. `edited_at` is the owner's last change |
| `PUT /meetings/{id}` | Registers a recording: `{started_at, ended_at?, app_version?}`, ISO 8601 with offset. Writes `meta.json` and the row (`uploading`), and returns a presigned PUT for the audio. Again while `uploading` or `failed`: a fresh URL. Otherwise 409. 429 while 10 of the person's other meetings are still being summarised; the apps send it again at their next sync |
| `POST /meetings/{id}/uploaded` | The upload finished: `uploading` becomes `queued`. The upload itself started the job |
| `GET /meetings/{id}` | The meeting, and once `ready` its `results` ([data.md](data.md)). Each speaker gets `clip_urls` and `candidates`, the 5 people closest in voice |
| `DELETE /meetings/{id}` | The meeting and everything Tally learned from it (below) |
| `PUT /meetings/{id}/speakers/{speaker}` | Who a speaker is: `{person_id}`, `{name, role?, company?}`, `{unknown: true}` or `{background: true}` (below) |
| `PUT /meetings/{id}/summary` | The owner's own copy of the summary: `{summary}`. Its title becomes the meeting's |
| `POST /meetings/{id}/summary/rewrite` | Writes the summary again without the background voices: `{replace?}` |
| `DELETE /meetings/{id}/summary` | Back to the agent's summary |
| `PUT /meetings/{id}/folders/{folder}` | Adds the meeting to a folder, besides any it's in |
| `DELETE /meetings/{id}/folders/{folder}` | Takes it out of that folder |
| `GET /folders` | The owner's folders: `{folder_id, name, parent}` |
| `PUT /folders/{id}` | Makes or changes a folder: `{name, parent?}`. The parent must be the owner's, and no loops |
| `DELETE /folders/{id}` | The folder and those inside it. Their meetings stay |
| `GET /meetings/{id}/files` | The meeting's files, with presigned GETs. A changed list moves `edited_at` |
| `POST /meetings/{id}/files` | Where to upload a file: `{name, size, type?}`, up to 200 MB. Returns a presigned PUT for exactly that size |
| `DELETE /meetings/{id}/files/{file}` | The file |
| `GET /search?q=` | `{results: [{meeting_id, title, started_at, where, snippet, at?}]}`: ready meetings with every word of `q`, newest first, up to 50. `where` is `title`, `summary`, `people` or `transcript` (with `at`, in seconds) |
| `GET /people` | The owner's people, with their voiceprints' ids and sources, by name |
| `PATCH /people/{id}` | Any of `{name, role, company, is_owner}` |
| `DELETE /people/{id}` | The person and all their voiceprints |
| `POST /crashes` | A crash report: `{report, app_version?}`, up to 2 MB. Kept at `crashes/<sub>/<ulid>.ips`, and emailed through SNS to config's `crashEmail` when set |
| `GET /admin/stats` | The admin dashboard's figures (below) |

Presigned URLs are valid for an hour.

## States

A meeting's `state` is in its row: the API writes `uploading` and `queued`, the job that claimed it `processing`, `ready` and `failed`.

```
uploading → queued → processing → ready
                         └──────→ failed
```

A meeting still `uploading`, `queued` or `processing` after 3 hours (the job's limit is 2) is reported as `failed`.

## The rules

**Naming a speaker:**
1. A meeting that isn't ready (409), an unknown person (404) or an empty body (400) changes nothing.
2. The new identity is written to `results/meeting.json` with `by: owner`. The write is conditional on the ETag read, and retried, so two quick confirmations both stick.
3. Any voiceprint this meeting gave this speaker is deleted.
4. For a person (a new one is created, `edited: true`), the speaker's centroid from `work/embeddings.npz` is saved as a `user` voiceprint, if it's a sound one. A background voice keeps none.
5. A person the agent brought in with this meeting, whom the owner says it wasn't, is deleted, unless anything else holds on to them.

**Deleting a meeting** removes everything under its prefix, its row, its voiceprints, and the people it brought in whom nothing else holds on to. A job still running for it loses its claim and stops.

**Editing a person:** a new name keeps the old one as an alias, so the agent still recognises it. `is_owner: true` takes it from anyone else. Every edit sets `edited: true`: such a person is never pruned, and the agent only fills in what they left empty.

**The owner's copy of a summary** has the summary's shape ([data.md](data.md)), keeping only the fields the apps read. Texts are at most 2,000 characters (the title 200), each naming speakers at most 20 times, and the whole at most 200 KB. It's kept in `notes/summary.json`, never over the agent's.

**Rewriting a summary:**
- 400 when the summary was already written without exactly the voices set aside now.
- 409 while a rewrite is on its way (up to 30 minutes), or when the owner has their own copy, unless `replace` is set. The copy then goes only once the new summary is written.
- It submits a summary-only job. `rewriting` is set until it's done, or `rewrite_error` says why not.

**The admin dashboard** (`GET /admin/stats`) is for admins only: `admin` in the token's claim at config's `auth.adminClaim`. It returns counts, timings and costs across everyone, never what a meeting says. It's the one route the site's page calls, so CORS allows GETs from the site; each still needs the caller's own token.

## What the app does

1. **Record.** No sign-in needed; recordings wait on the computer.
2. **Send.** `PUT /meetings/{id}`, PUT the audio to `upload_url`, then `POST /meetings/{id}/uploaded`. A failure retries from the step it reached.
3. **Poll.** `GET /meetings` every 15 s while anything is on its way, otherwise every 60 s.
4. **Read.** `GET /meetings/{id}` when one is opened, and again after 50 minutes, before its links expire.
5. **Tidy up.** When a sent meeting is `ready`, or gone, the app deletes its local recording.

The job's log (steps and timings only) is in the stack's `LogGroup` output, and the agent's conversation in `run/conversation.json`.
