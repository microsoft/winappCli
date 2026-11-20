using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace TodoBench;

public sealed partial class Contact
{
    public string Name { get; set; } = "";
    public string Email { get; set; } = "";
}

public sealed partial class ContactsVm : INotifyPropertyChanged
{
    private Contact? _selected;
    private string _title = "Team";

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<Contact> Contacts { get; } = [];
    public Contact? Selected { get => _selected; set { _selected = value; Raise(); } }
    public string Title { get => _title; set { _title = value; Raise(); } }

    public ContactsVm()
    {
        foreach (var name in new[] { "Ada", "Grace", "Linus" })
        {
            Contacts.Add(new Contact { Name = name, Email = $"{name.ToLowerInvariant()}@contoso.com" });
        }
    }

    private void Raise([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
