Add-Type -AssemblyName PresentationFramework
Add-Type -AssemblyName PresentationCore
Add-Type -AssemblyName WindowsBase

[xml]$xaml = @"
<Window xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        Title="MCP Test Application" Height="650" Width="720"
        WindowStartupLocation="CenterScreen">
    <Grid Margin="10">
        <Grid.RowDefinitions>
            <RowDefinition Height="Auto"/>
            <RowDefinition Height="Auto"/>
            <RowDefinition Height="Auto"/>
            <RowDefinition Height="*"/>
            <RowDefinition Height="Auto"/>
            <RowDefinition Height="Auto"/>
        </Grid.RowDefinitions>

        <!-- Row 0: Text Input -->
        <GroupBox Header="Text Input" Grid.Row="0" Margin="5">
            <StackPanel Margin="5">
                <StackPanel Orientation="Horizontal" Margin="0,3">
                    <Label Content="Name:" Width="80"/>
                    <TextBox Name="NameBox" Width="250" Text="Test User"
                             AutomationProperties.Name="Name Input"/>
                </StackPanel>
                <StackPanel Orientation="Horizontal" Margin="0,3">
                    <Label Content="Email:" Width="80"/>
                    <TextBox Name="EmailBox" Width="250" Text="test@example.com"
                             AutomationProperties.Name="Email Input"/>
                </StackPanel>
            </StackPanel>
        </GroupBox>

        <!-- Row 1: Checkboxes and Radio Buttons -->
        <Grid Grid.Row="1">
            <Grid.ColumnDefinitions>
                <ColumnDefinition Width="*"/>
                <ColumnDefinition Width="*"/>
            </Grid.ColumnDefinitions>

            <GroupBox Header="Checkboxes" Grid.Column="0" Margin="5">
                <StackPanel Margin="5">
                    <CheckBox Name="CheckNotifications" Content="Enable notifications"
                              IsChecked="True" Margin="0,3"/>
                    <CheckBox Name="CheckDarkMode" Content="Dark mode"
                              IsChecked="False" Margin="0,3"/>
                    <CheckBox Name="CheckAutoSave" Content="Auto-save"
                              IsChecked="False" Margin="0,3"/>
                </StackPanel>
            </GroupBox>

            <GroupBox Header="Radio Buttons" Grid.Column="1" Margin="5">
                <StackPanel Margin="5">
                    <RadioButton Content="Small" GroupName="Size" Margin="0,3"/>
                    <RadioButton Content="Medium" GroupName="Size" IsChecked="True" Margin="0,3"/>
                    <RadioButton Content="Large" GroupName="Size" Margin="0,3"/>
                </StackPanel>
            </GroupBox>
        </Grid>

        <!-- Row 2: Dropdown and Slider -->
        <Grid Grid.Row="2">
            <Grid.ColumnDefinitions>
                <ColumnDefinition Width="*"/>
                <ColumnDefinition Width="*"/>
            </Grid.ColumnDefinitions>

            <GroupBox Header="Dropdown" Grid.Column="0" Margin="5">
                <StackPanel Orientation="Horizontal" Margin="5">
                    <Label Content="Priority:"/>
                    <ComboBox Name="PriorityCombo" Width="150" SelectedIndex="1"
                              AutomationProperties.Name="Priority">
                        <ComboBoxItem Content="Low"/>
                        <ComboBoxItem Content="Medium"/>
                        <ComboBoxItem Content="High"/>
                        <ComboBoxItem Content="Critical"/>
                    </ComboBox>
                </StackPanel>
            </GroupBox>

            <GroupBox Header="Slider" Grid.Column="1" Margin="5">
                <StackPanel Orientation="Horizontal" Margin="5">
                    <Label Content="Volume:"/>
                    <Slider Name="VolumeSlider" Width="200" Minimum="0" Maximum="100"
                            Value="50" AutomationProperties.Name="Volume"
                            TickPlacement="BottomRight" TickFrequency="10"/>
                </StackPanel>
            </GroupBox>
        </Grid>

        <!-- Row 3: Data Table -->
        <GroupBox Header="Data Table" Grid.Row="3" Margin="5">
            <DataGrid Name="TaskTable" AutoGenerateColumns="False" IsReadOnly="True"
                      CanUserAddRows="False" AutomationProperties.Name="Task Table">
                <DataGrid.Columns>
                    <DataGridTextColumn Header="Task Name" Binding="{Binding Name}" Width="180"/>
                    <DataGridTextColumn Header="Status" Binding="{Binding Status}" Width="100"/>
                    <DataGridTextColumn Header="Priority" Binding="{Binding Priority}" Width="100"/>
                    <DataGridTextColumn Header="Due Date" Binding="{Binding DueDate}" Width="120"/>
                </DataGrid.Columns>
            </DataGrid>
        </GroupBox>

        <!-- Row 4: Buttons -->
        <StackPanel Grid.Row="4" Orientation="Horizontal" Margin="10,5" HorizontalAlignment="Center">
            <Button Name="SubmitBtn" Content="Submit" Width="80" Margin="5"
                    AutomationProperties.Name="Submit"/>
            <Button Name="ResetBtn" Content="Reset" Width="80" Margin="5"
                    AutomationProperties.Name="Reset"/>
            <Button Name="CancelBtn" Content="Cancel" Width="80" Margin="5"
                    AutomationProperties.Name="Cancel"/>
            <Label Name="StatusLabel" Content="Ready" Foreground="Green" Margin="20,0,0,0"
                   AutomationProperties.Name="Status"/>
        </StackPanel>

        <!-- Row 5: Scrollable Notes -->
        <GroupBox Header="Notes" Grid.Row="5" Margin="5" Height="100">
            <TextBox Name="NotesBox" AcceptsReturn="True" TextWrapping="Wrap"
                     VerticalScrollBarVisibility="Auto"
                     AutomationProperties.Name="Notes"
                     Text="This is a scrollable text area for testing.&#x0A;Line 2 of notes.&#x0A;Line 3 of notes.&#x0A;Line 4 of notes.&#x0A;Line 5 of notes."/>
        </GroupBox>
    </Grid>
</Window>
"@

$reader = (New-Object System.Xml.XmlNodeReader $xaml)
$window = [Windows.Markup.XamlReader]::Load($reader)

# Get named elements
$nameBox = $window.FindName("NameBox")
$emailBox = $window.FindName("EmailBox")
$checkNotif = $window.FindName("CheckNotifications")
$checkDark = $window.FindName("CheckDarkMode")
$checkAuto = $window.FindName("CheckAutoSave")
$combo = $window.FindName("PriorityCombo")
$slider = $window.FindName("VolumeSlider")
$table = $window.FindName("TaskTable")
$submitBtn = $window.FindName("SubmitBtn")
$resetBtn = $window.FindName("ResetBtn")
$statusLabel = $window.FindName("StatusLabel")
$notesBox = $window.FindName("NotesBox")

# Populate table
$data = @(
    [PSCustomObject]@{Name="Write unit tests"; Status="In Progress"; Priority="High"; DueDate="2026-03-10"}
    [PSCustomObject]@{Name="Fix login bug"; Status="Open"; Priority="Critical"; DueDate="2026-03-08"}
    [PSCustomObject]@{Name="Update docs"; Status="Done"; Priority="Low"; DueDate="2026-03-15"}
    [PSCustomObject]@{Name="Code review"; Status="In Progress"; Priority="Medium"; DueDate="2026-03-09"}
    [PSCustomObject]@{Name="Deploy v2.0"; Status="Open"; Priority="High"; DueDate="2026-03-12"}
)
$table.ItemsSource = $data

# Button handlers
$submitBtn.Add_Click({
    $statusLabel.Content = "Submitted: $($nameBox.Text)"
    $statusLabel.Foreground = [System.Windows.Media.Brushes]::Blue
})

$resetBtn.Add_Click({
    $nameBox.Text = "Test User"
    $emailBox.Text = "test@example.com"
    $checkNotif.IsChecked = $true
    $checkDark.IsChecked = $false
    $checkAuto.IsChecked = $false
    $combo.SelectedIndex = 1
    $slider.Value = 50
    $statusLabel.Content = "Reset complete"
    $statusLabel.Foreground = [System.Windows.Media.Brushes]::Green
})

$window.ShowDialog() | Out-Null
