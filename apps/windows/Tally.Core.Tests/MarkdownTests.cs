namespace Tally.Tests;

/// <summary>Copy Summary and Copy Transcript: one Markdown, which the meetings folder writes too.</summary>
public class MarkdownTests
{
    [Fact]
    public void Copy_summary_gives_everything_the_page_shows_with_names()
    {
        var results = Fixtures.Results("""
            {"language": "en", "duration": 4000,
             "summary": {"title": "Q3 pricing", "overview": "{S1} and {S2} set the price.", "summary": ["{S2} proposed $45."],
                         "decisions": [{"text": "Go ahead.", "by": ["S1", "S2"]}],
                         "action_items": [{"owner": "S2", "text": "Send it.", "due": "2026-11-03"}, {"owner": null, "text": "Book a room.", "due": null, "done": true}],
                         "open_questions": [{"text": "Support?", "raised_by": ["S1"]}],
                         "topics": [{"title": "Enterprise tier", "start": 60, "end": 3725, "notes": ["{S1} agreed."], "outcome": "Approved."}],
                         "attendees": [{"speaker": "S1", "role": "Chairs", "contribution": "Made the call."},
                                       {"speaker": "S2", "role": "Sales", "contribution": "Proposed it."}]},
             "speakers": {"S1": {"talk_time": 900, "identity": {"name": "Ada", "person_id": "p_1", "by": "owner"}},
                          "S2": {"talk_time": 600, "identity": {"unknown": true, "by": "agent"}}},
             "segments": [{"start": 0, "end": 4, "speaker": "S1", "text": "Shall we?"}, {"start": 4, "end": 6, "speaker": "S1", "text": "Now."},
                          {"start": 3700, "end": 3705, "speaker": "S2", "text": "Yes."}]}
            """);
        var names = new Names(results, new Dictionary<string, Person> { ["p_1"] = Fixtures.Person("p_1", "Ada Lovelace", "CFO", "Acme") });
        Assert.Equal("""
            # Q3 pricing

            **Participants:** Ada Lovelace (CFO, Acme), Speaker 2

            Ada Lovelace and Speaker 2 set the price.

            ## Summary
            - Speaker 2 proposed $45.

            ## Decisions
            - Go ahead. (Ada Lovelace, Speaker 2)

            ## Action items
            - [ ] Speaker 2: Send it. (by 2026-11-03)
            - [x] Book a room.

            ## Open questions
            - Support? (Ada Lovelace)

            ## Topics

            ### Enterprise tier (1:00–1:02:05)
            - Ada Lovelace agreed.

            **Outcome:** Approved.

            ## Who said what
            - **Ada Lovelace**, CFO, Acme. Made the call.
            - **Speaker 2**, Sales. Proposed it.
            """, Markdown.Summary(results.Summary, names));
        Assert.Equal("**Ada Lovelace** (0:00)\nShall we? Now.\n\n**Speaker 2** (1:01:40)\nYes.", Markdown.Transcript(results, names));
    }
}
