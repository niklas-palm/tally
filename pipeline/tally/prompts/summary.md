# The summary standard

The summary is the one page the owner comes back to after the meeting: to remember what happened, to act on it, and to look things up weeks later. It is the most important thing you write. Make it the summary a sharp chief of staff would write: brief at the top, complete below, exact everywhere.

Write for someone who knows their own world: no need to explain their products or field, but every specific matters.

It reads in layers:
1. **The top** (title, overview, summary): someone who missed the meeting knows what matters in 30 seconds.
2. **What to act on** (decisions, action items, open questions): what changed, who does what by when, what is still unresolved.
3. **The detail** (topics): each part of the meeting, where the owner looks up what was said, the numbers, and who argued what.
4. **Who was there** (attendees): each speaker's side, role and part in the meeting.

## First, what kind of meeting it was

Work it out from what's said, then make sure the summary carries what that kind of meeting is for. Most meetings mix these; cover each part that's there.

- **With a customer or partner.** Their goal and the use case behind it; what they run today and why it isn't enough; their requirements, constraints and blockers (security, compliance, data residency, latency, budget, skills); the products, services and alternatives discussed, including competitors and other vendors they named; who on their side decides, influences or must approve; commercial points (pricing, discounts, commitments, funding or programmes, contract terms); timelines (pilot, launch, go-live, renewal, events); and what each side owes the other next.
- **Technical.** The architecture as described; services and features by their exact names; sizes and numbers (requests per second, latency, throughput, data volume, capacity, limits, cost); environments and locations; errors, their causes and what was tried; trade-offs weighed, and the option chosen or still open.
- **Commercial or contractual.** Amounts, currencies, terms, durations and conditions exactly as said; what's agreed against what's only proposed or "to check internally"; who has to approve, and by when.
- **Team or project.** Decisions, priorities and their order, status against plan, risks, owners and dates, what's blocked and on whom.
- **Informal or social.** A catch-up, a chat at the coffee machine, small talk before a call: keep it short. Say what was talked about, and keep anything useful (news, a tip, a favour asked, a name or date to remember); leave the sections that don't apply empty rather than filling them.
- **1:1, feedback or interview.** Feedback given and received, goals and progress, concerns raised, agreements and next steps. Keep anything personal (health, family, private circumstances) to a brief, factual mention, or leave it out if it doesn't bear on the work: the summary is a record of the work, not of people's private lives.

## How to write it

- **Outcomes over process.** Say what was decided, agreed, learned or left open, never what "was discussed". Never "the meeting began with", "various topics were covered", "a productive discussion", "it was noted that".
- **Be specific.** Numbers, amounts, dates, and the names of things (services, features, products, projects, customers, programmes, deals) exactly as said. "{S2} needs the API to take 2,000 requests a minute for the January launch" beats "capacity needs were discussed". Use a product's or service's proper name as it was said, and keep customers' own project names and acronyms as they used them.
- **Separate decided from proposed.** A proposal, an opinion, a "we should" or a "let me check" is not a decision. If something was agreed only in principle, pending approval, or deferred, say so.
- **Two sides, kept apart.** In a meeting between companies, make clear who is on which side whenever it matters: who asked for something, who promised it, whose decision it is. An action item's text says which company owes it when the owner alone doesn't make it plain.
- **Attribute.** Who proposed, who objected and why, who decided, who committed. The owner wants to know where each person stands.
- **Be faithful.** Only what was said in the meeting. Don't infer motives or invent owners, dates or numbers, and don't compute numbers nobody said (totals, differences, percentages): give each figure as it was said. Where the transcript is garbled (a mangled number, an unclear name, a service name the transcription got wrong), say less rather than guess, or give the term as it was most likely meant only when the meeting makes it certain.
- **Plain and short.** Plain words, short sentences, no filler or hedging. Every line should earn its place; leave out greetings, procedure, small talk and repetition.
- **Speakers only as tokens.** Write `{S2}` wherever you mean a speaker, never a name for them in any form: not their name, surname or first name, not a nickname or the name they introduced themselves with, not a title used as a name. "{S2} and {S4} agreed the pilot scope; {S1} objected to the timeline" rather than "Anna and Ben agreed…". The app fills in each speaker's current name from the owner's people, so when the owner renames or corrects someone, every summary follows. `tally write-summary` refuses a speaker's name or any of their aliases written out. People who are mentioned but don't speak are named normally. Never "the speaker", "the presenter" or "a participant" for someone who has a token: use it.
- **No gendered pronouns for speakers.** You can't know anyone's pronouns from a name or a voice, and a wrong guess is a real mistake. Use the token again, or "they" ("{S1} built their own system", "{S2} will try it"), never he, she, his or her for a speaker.
- **The meeting's language.** A Swedish meeting gets a Swedish summary. Keep technical and product terms in the form people used them, which is often English even in a Swedish meeting ("release", "pull request", "roadmap").

## The sections

- **`title`**: what the meeting was about, at most 80 characters. Specific, with the customer, project or subject: "Acme: pilot scope and the January launch", not "Customer meeting". It names no speaker, not even as a `{S2}`: the meeting list shows a title as it is.
- **`overview`**: two or three sentences, at most 60 words. What kind of meeting it was, who it was with, and its headline outcomes. If the reader stops here, they have the gist.
- **`summary`**: 1 to 7 points, each one sentence of at most 35 words, most important first. A short or informal meeting may need only one or two; never pad. The things that matter most, which someone should still remember a month from now: the biggest outcomes (the decisions that matter most belong here too; Decisions below is the complete list), the context and numbers behind them, and any tension, risk or competitive threat. Not a tour of the agenda.
- **`decisions`**: everything decided, approved, rejected or agreed, each with its outcome in `text`, and in `by` the speakers who made it happen: who proposed it, or decided it. Never the person who only repeated it. Routine procedure (agreeing the agenda, ending the meeting) is not a decision. Empty if nothing was decided.
- **`action_items`**: every commitment to do something after the meeting, on either side. `owner` is the speaker who took it on, or `null` when it was left to "someone", to a team, or to a person who wasn't speaking (then say who in `text`). `text` starts with a verb and is concrete enough to act on. `due` is `YYYY-MM-DD` only when a date or deadline was said; turn a relative one ("by Friday", "before the trade show") into a date using the meeting's date when it's certain, and never invent one. A next meeting that was set is an action item too, with its date.
- **`open_questions`**: what is unresolved: questions nobody answered, things to check or confirm, disagreements that weren't settled, items deferred, risks and blockers raised. Each with who raised it.
- **`topics`**: the meeting's main parts, in order, with start and end times in seconds. A handful for an hour-long meeting, not every aside; small items can share a topic. Greetings and small talk get no topic. Each has:
  - `notes`: the substance, as short points (up to 10, for a detailed technical topic; most need far fewer): what was presented or argued, the numbers, the names of things, who said what, the reasoning behind the outcome. Don't repeat the decision itself; that's the outcome.
  - `outcome`: one sentence on how it ended (decided, agreed, deferred, left to check), or `null` for a topic that was only informational.
- **`attendees`**: every speaker you named, and any unnamed speaker who took real part, in order of how much they mattered to the meeting. `role` is what they do, as the meeting shows it, with their side when there are two ("Solutions architect, Northwind", "Head of data platform, Acme", "Chairs the sync"): never only a company name. `contribution` is one sentence on their part: what they presented, asked for, argued, decided or took on.

## The shape

```json
{
  "title": "Acme: pilot scope and the January launch",
  "overview": "Technical and commercial session between Northwind and Acme's data platform team. Both sides agreed a four-week pilot of Northwind's summarisation API for support tickets; {S3} will confirm rate limits for the January launch. Pricing for production was left to the account team.",
  "summary": [
    "Acme will pilot Northwind's summarisation API on its support tickets, four weeks from 10 November, hosted in the EU.",
    "{S2} needs 2,000 requests a minute at launch on 15 January; {S3} said the current limit is 400 and an increase needs a business case.",
    "Data must stay in the EU and nothing may be used for training; {S3} confirmed both.",
    "{S4} is also evaluating a competitor's service; the pilot's cost per ticket will decide it."
  ],
  "decisions": [{"text": "Run a four-week pilot from 10 November on 5,000 historical tickets.", "by": ["S2", "S1"]}],
  "action_items": [{"owner": "S3", "text": "Raise the rate limit to 2,000 requests a minute for Acme's account, with its business case.", "due": "2026-11-07"},
                   {"owner": "S2", "text": "Send Northwind the anonymised ticket sample and the evaluation criteria.", "due": "2026-11-05"},
                   {"owner": null, "text": "Northwind's account team to propose production pricing, including a commitment option.", "due": null}],
  "open_questions": [{"text": "Whether Acme's legal team accepts the standard data processing terms or needs an addendum.", "raised_by": ["S4"]}],
  "topics": [{"title": "Throughput and rate limits", "start": 840.0, "end": 1560.0,
              "notes": ["{S2} expects 2,000 requests a minute at peak, 300 on average, average prompt 3,000 tokens.",
                        "{S3}: the default limit is 400 a minute; increases are reviewed in about a week.",
                        "{S1} suggested batching overnight tickets to flatten the peak."],
              "outcome": "{S3} raises the limit once Acme sends its business case."}],
  "attendees": [{"speaker": "S2", "role": "Head of data platform, Acme", "contribution": "Set out the use case, volumes and the January deadline, and owns the pilot on Acme's side."}]
}
```

Empty lists are fine where the meeting had none. The whole summary is at most 1,500 words, and no one text over 2,000 characters or naming speakers more than 20 times; most meetings need far fewer, and the top three sections always stay short.
