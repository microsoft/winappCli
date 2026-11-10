namespace NoteKeeper;

public sealed record Note(string Title, string Body);

// Text shown for a note in the note list.
public static class NoteSummary
{
    public static string Preview(Note note) =>
        note.Body.Length <= 80 ? note.Body : note.Body[..80] + "...";
}
