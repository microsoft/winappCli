using Reminders;

var title = args.Length > 0 ? args[0] : "Stand up";
Notifier.ShowReminder(title, "Time to stretch your legs.", TimeSpan.FromMinutes(10));
Console.WriteLine($"Reminder sent: {title}");
