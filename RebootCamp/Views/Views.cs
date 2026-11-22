using System.Windows.Controls;

namespace RebootCamp.Views;

// Le « code-behind » des cinq écrans. Il ne fait que charger le XAML :
// toute la logique est dans les ViewModels (pattern MVVM).

public partial class AccueilView : UserControl
{
    public AccueilView() => InitializeComponent();
}

public partial class PilotesView : UserControl
{
    public PilotesView() => InitializeComponent();
}

public partial class LogicielsView : UserControl
{
    public LogicielsView() => InitializeComponent();
}

public partial class NettoyageView : UserControl
{
    public NettoyageView() => InitializeComponent();
}

public partial class ComposantsView : UserControl
{
    public ComposantsView() => InitializeComponent();
}

public partial class DesinstallerView : UserControl
{
    public DesinstallerView() => InitializeComponent();
}

public partial class ExtrasView : UserControl
{
    public ExtrasView() => InitializeComponent();
}
