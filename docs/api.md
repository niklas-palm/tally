# API

The contract between the apps (Mac and Windows) and the cloud: an API Gateway HTTP API (the stack's `ApiUrl` output) with one Lambda behind it, `api/tally_api.py`. Keep this file true when either side changes.

## Access

- **Every route needs an access token** for the app's client, from the OIDC provider in config's `auth` (the stack's `Issuer` and `ClientId` outputs), or from the Cognito user pool the stack makes without one.
  - The client is public, uses authorization code with PKCE S256, and redirects to `tally://callback` and the site's `admin.html`.
  - The token names the client: in its `aud` (Keycloak: an audience mapper), or in Cognito's `client_id`.
  - Who may sign in is the provider's business. In a Keycloak realm, a client role granted to groups is one way: only people whose groups hold it get a token.
- **The authorizer:** API Gateway's JWT authorizer checks the signature, issuer, audience and expiry before the function runs. Requests without a valid token get 401.
- **Session length:** the app renews short-lived access tokens with its refresh token for as long as the provider's session lasts.
- **Everything is the caller's.** The function takes the token's `sub` (the provider's user id: letters, digits, `-` and `_`, up to 128) as the owner on every request:
  - meetings live under `users/<sub>/meetings/<id>/`
  - every table is keyed by `owner` first
  - another user's meeting or person is simply not found (404)

  Everyone who signs in gets a Tally of their own, sharing only the pipeline.
- **The function's role** reaches `users/*` and `crashes/*` in the data bucket, the five tables and the crash topic; and, for the admin dashboard and summary rewrites, Batch's ListJobs and DescribeComputeEnvironments, SubmitJob on its own queue and job definition only, and Cost Explorer. The app holds no AWS credentials.

## Routes

Every request from the app carries `X-Tally-Version` (its version, or `repository build`), and `GET /meetings` records it with the token's name and email and the time in the `users` table, for the admin dashboard.

Bodies and replies are JSON with snake_case keys. Errors are `{"error": "..."}`:

| Status | When |
|---|---|
| 400 | A bad request, including a body that isn't valid Unicode (a lone half of an emoji's pair) |
| 401 | No valid token (from the authorizer) |
| 403 | A token whose user id isn't one, or a caller on `/admin/stats` who isn't an admin |
| 404 | Something that isn't there, or isn't the caller's |
| 409 | A meeting sent already; one not ready yet (naming a speaker, editing or rewriting its summary); one that changed meanwhile; editing a summary being rewritten; a rewrite that would replace the owner's copy without `replace` |
| 500 | Anything unexpected, with a plain message |

Meeting ids are ULIDs made by the app. A person's name, role and company are one line of at most 100 characters each, because they reach the meeting agent's prompt.

| Route | What it does |
|---|---|
| `GET /meetings` | Every meeting, newest first: `{meetings: [{meeting_id, state, title, started_at, ended_at, duration, language, error, updated_at, edited_at, rewriting, rewrite_error, people, folder_ids}]}`; `rewriting` set while a summary rewrite is on its way and `rewrite_error` why the last one didn't happen; `edited_at` the owner's last change to it (its summary, who spoke, its folders or files, also when a listing of its files finds others than it last saw), for a copy kept elsewhere to know to fetch it again; `folder_ids` the owner's folders it's in, any number |
| `PUT /meetings/{id}` | Registers a recording. Body `{started_at, ended_at?, app_version?}`: the times ISO 8601 to the second with their offset (at most ±14:00, all .NET reads), this century (`2026-10-28T09:00:00Z`, the form both apps write and read; 400 otherwise). It writes `meta.json` and the meeting's row (state `uploading`), and returns `{upload_url, upload_headers}`: a presigned PUT for `users/<sub>/meetings/<id>/audio.m4a`, valid for an hour. Again for one still `uploading` or `failed` (Send Again): a fresh URL. For one already on its way or ready: **409**, so a repeated send can never start a second run or overwrite the owner's confirmations. |
| `POST /meetings/{id}/uploaded` | The upload finished. An `uploading` meeting becomes `queued`; one the job has already moved on is left as it is. The pipeline was started by the upload itself (S3 → EventBridge → Batch). |
| `GET /meetings/{id}` | The meeting. Once `ready`, it also has `results`, the pipeline's `results/meeting.json` ([data.md](data.md)). Each speaker gets `clip_urls` (presigned GETs, valid for an hour) and `candidates`: the owner's people whose voiceprints come closest, `[{person_id, name, score}]`, the top 5 by best cosine similarity, computed when asked, so people confirmed later count. |
| `DELETE /meetings/{id}` | The meeting and everything Tally learned from it. Returns `{files, voiceprints, people}`, the counts removed. See below. |
| `PUT /meetings/{id}/speakers/{speaker}` | The owner says who a speaker is: `{person_id}` (someone known), `{name, role?, company?}` (someone new), `{unknown: true}`, or `{background: true}` (a voice nearby, not in the meeting: nobody, whatever else the body says, and no voiceprint is kept). Returns `{speaker, identity}`. See below. |
| `PUT /meetings/{id}/summary` | The owner's own copy of a ready meeting's summary: `{summary}`. Its title becomes the meeting's. See below. |
| `POST /meetings/{id}/summary/rewrite` | Writes the summary again without the voices set aside as background: `{replace?}`. See below. |
| `DELETE /meetings/{id}/summary` | Back to the agent's summary: the copy goes, and the title is the agent's again. |
| `PUT /meetings/{id}/folders/{folder}` | Adds the meeting to one of the owner's folders (404 if it isn't), besides any it's in: adding twice is once. |
| `DELETE /meetings/{id}/folders/{folder}` | Takes it out of that folder; it stays in any others. |
| `GET /folders` | `{folders: [{folder_id, name, parent}]}`, the owner's folders; `parent` null at the top. |
| `PUT /folders/{id}` | Makes or changes a folder: `{name, parent?}`, a name of 1 to 100 characters. The id is the app's, `f_<ulid>`. The parent must be the owner's folder (404 otherwise), and not this one or one inside it, nor any loop (400). |
| `DELETE /folders/{id}` | The folder and the folders inside it. Their meetings stay, no longer in them. Returns `{folders}`, how many went. |
| `GET /meetings/{id}/files` | `{files: [{file_id, name, size, added_at, url}]}`, oldest first; `url` is a presigned GET, valid for an hour. When they aren't the files the meeting's row last saw (its `file_ids`), `edited_at` moves to now, so another computer's meetings folder, which may have looked while an upload was still going, fetches them: the apps list a meeting's files once an upload is done. |
| `POST /meetings/{id}/files` | Where to upload a file: `{name, size, type?}`, a name of 1 to 200 characters without `/`, `\` or control characters, and not `.` or `..`, and 1 byte to 200 MB; `type`, its content type (`image/png`), `application/octet-stream` when absent. Returns `{file_id, upload_url, upload_headers}`: a presigned PUT, signed for exactly that size, valid for an hour. |
| `DELETE /meetings/{id}/files/{file}` | The file. |
| `GET /admin/stats` | The admin dashboard's figures, for Tally's admins only. See below. |
| `GET /search?q=` | `{results: [{meeting_id, title, started_at, where, snippet, at?}]}`: ready meetings with every word of `q`, newest first, up to 50. `where` is `title`, `summary` (the owner's copy if any, with names), `people` or `transcript` (with `at`, the second it was said). |
| `GET /people` | `{people: [{person_id, name, aliases, role, company, is_owner, voiceprints: [{voiceprint_id, source, meeting_id, created_at}]}]}`, by name |
| `PATCH /people/{id}` | Any of `{name, role, company, is_owner}`. An empty role or company clears it. |
| `DELETE /people/{id}` | The person and all their voiceprints (their meetings keep their own audio and speakers' centroids until they're deleted) |
| `POST /crashes` | A crash report from the Mac or Windows app: `{report, app_version?}`, the report's text up to 2 MB; who sent it is the token's (its name, email or username). It's kept at `crashes/<sub>/<ulid>.ips`, and an email with its gist (for a Mac's report: when, macOS, the exception, the top of the crashed thread; a Windows report, plain text already, as its first lines) goes through SNS to config's `crashEmail`, when one is set. Returns `{key}`. |

## States

A meeting's `state` lives in its row in the meetings table, written by the API (`uploading`, `queued`) and by the job that claimed it (`processing`, `ready`, `failed`):

```
uploading → queued → processing → ready
                         └──────→ failed
```

A meeting `uploading`, `queued` or `processing` whose `updated_at` is more than 3 hours old (the job's limit is 2 hours, plus waiting for a GPU) is reported as `failed` ("Summarising stopped before it finished."). That's a job that died without saying so.

## The rules

**Naming a speaker** (`PUT /meetings/{id}/speakers/{speaker}`), in this order:
1. **Check the request.** A meeting that isn't ready (409), an unknown person (404) or an empty body (400) changes nothing.
2. **Write the new identity to `results/meeting.json`:** `{name, person_id, confidence: high, by: owner, ...}`, or `{unknown: true, reason, by: owner}` (with `background: true` for a background voice, [data.md](data.md)).
   - The write is conditional on the ETag of the version that was read. If the file changed meanwhile, it's read again, so two quick confirmations in one meeting both stick.
   - If the meeting is gone, the answer is 404.
3. **Delete this speaker's old voiceprint.** Any voiceprint this meeting gave this speaker goes, whoever it was saved for: voiceprints record their `speaker`.
4. **For a person, save the voice.** A new person is created, with `first_meeting_id` and `edited: true`. Then this speaker's centroid, read from the meeting's `work/embeddings.npz`, is saved as a `source: user` voiceprint, which future matches trust. That happens only for a centroid of the model's 256 numbers, normalised: otherwise the identity still changes, but no voiceprint is saved.
5. **Prune.** A person the agent brought in with this meeting, whom the owner has now said it wasn't, is deleted, unless anything else holds on to them: a voiceprint, another speaker here or in another meeting, being the owner, or an owner's edit.

**Deleting a meeting** (`DELETE /meetings/{id}`) removes the following. A job still running for it loses its claim on the meeting and stops, taking back anything it wrote after the delete:
- every object under `users/<sub>/meetings/<id>/`
- the meeting's row
- every voiceprint from the meeting
- the people the meeting brought in (`first_meeting_id`) who have no voiceprints left, whom no other meeting names, and that the owner never edited or marked as themselves

**Editing a person** (`PATCH /people/{id}`):
- A new name keeps the old one as an alias, so the agent still recognises it in meetings.
- `is_owner: true` takes it from anyone else: there is one owner.
- Every edit sets `edited: true`. A person the owner has touched is never pruned, and the agent only fills in a role or company they left empty.

**The owner's copy of a summary** (`PUT /meetings/{id}/summary`):
- The body has the summary's shape ([data.md](data.md)), and only the fields the apps read are kept:
  - title, overview and summary points as text
  - decisions with `text` and `by`
  - action items with `text`, and maybe `owner`, a `due` day (YYYY-MM-DD, this century) and `done` (true, false or null)
  - questions with `text` and `raised_by`
  - topics with a title, `start` and `end` in seconds, notes, and maybe an outcome
  - attendees with speaker, role and contribution
- The title is kept to its first 200 characters. Every other text is at most 2,000 characters, and names speakers (`{S2}`) at most 20 times, since search and the apps put a name of up to 100 characters in for each.
- The whole is at most 200 KB as JSON. Anything else is a 400 that says why.
- It's kept in `notes/summary.json`, never over the agent's, and `GET /meetings/{id}` then has it as `notes.summary`.

**Rewriting a summary** (`POST /meetings/{id}/summary/rewrite`):
- It's a 400 when the summary was already written without exactly the voices set aside now.
- It's a 409 while a rewrite is already on its way (`rewriting`, for up to 30 minutes).
- It's a 409 when the owner has their own copy, unless `replace` is set. The job then removes the copy once the new summary is written, so a rewrite that fails loses nothing. A copy the owner saves after asking stays.
- It submits a summary-only job. The meeting's `rewriting` is set until it's done, or `rewrite_error` says why not, also when the job couldn't be submitted.

**The admin dashboard's figures** (`GET /admin/stats`):
- Only admins may call it: `admin` in the token's claim at config's `auth.adminClaim` (`TALLY_ADMIN_CLAIM`). That's `resource_access.tally.roles` in Keycloak and `cognito:groups` in Cognito. Anyone else gets 403.
- It returns `{usage, people, failures, failed_week, turnaround, queue, crashes_week, cost, model, at}`. These are counts and timings across everyone, never what a meeting says.
- Meetings are counted `today` (since local midnight in config's `timezone`), in the last `three_days`, `week` and `month`, and `all`, in `usage` and for each person.
- `model` is the agent's runs since the 1st of the month, `{runs, rewrites, input, output, cache_read, cache_write, usd}`, priced from each meeting's `agent_runs` at config's `modelPrices`. Its `usd` is in `cost` too, because Bedrock bills Anthropic's models untagged.
- It's the one route the site's page calls. CORS lets the site (`SiteUrl`) make GET requests. An HTTP API's CORS covers the whole API, so that's every GET route, each still needing the signed-in person's own token.

## What the app does

1. **Record.** Recording needs no sign-in, and recordings wait on the computer.
2. **Send.** Once signed in: `PUT /meetings/{id}`, then PUT the audio to `upload_url` (the recording is now `uploaded` on the computer), then `POST /meetings/{id}/uploaded`. A failure retries from the step it reached; a 409 means it was sent already.
3. **Poll.** `GET /meetings` every 15 s while anything is on its way, otherwise every 60 s.
4. **Read.** `GET /meetings/{id}` when one is opened; again after 50 minutes, so clip links never expire mid-use.
5. **Tidy up.** When a sent meeting turns `ready`, or isn't listed any more (deleted on another device), the app deletes its local recording.

To try it by hand, with a token for the app's client (the stack's `ClientId` output), follow steps 2 to 4. The job's log (steps and timings only) is in the stack's `LogGroup` output, and the agent's whole conversation in `users/<sub>/meetings/<id>/run/conversation.json`.
