"""
Test application with various UI controls for exercising all Windows MCP tools.
Launch this app, then use the MCP tools to interact with it.

Controls included:
- Text entry fields (ValuePattern)
- Checkboxes (TogglePattern)
- Combobox / dropdown (ExpandCollapsePattern + SelectionItemPattern)
- Treeview / table (GridPattern)
- Buttons (InvokePattern)
- Labels / static text
- Scrollable text area
- Radio buttons
- Slider / scale (RangeValuePattern)
"""

import tkinter as tk
from tkinter import ttk


def create_test_app():
    root = tk.Tk()
    root.title("MCP Test Application")
    root.geometry("700x600")
    root.resizable(True, True)

    # --- Row 0: Text entry ---
    frame_entry = ttk.LabelFrame(root, text="Text Input", padding=10)
    frame_entry.grid(row=0, column=0, padx=10, pady=5, sticky="ew", columnspan=2)

    ttk.Label(frame_entry, text="Name:").grid(row=0, column=0, sticky="w")
    name_entry = ttk.Entry(frame_entry, width=30)
    name_entry.grid(row=0, column=1, padx=5)
    name_entry.insert(0, "Test User")

    ttk.Label(frame_entry, text="Email:").grid(row=1, column=0, sticky="w", pady=5)
    email_entry = ttk.Entry(frame_entry, width=30)
    email_entry.grid(row=1, column=1, padx=5, pady=5)
    email_entry.insert(0, "test@example.com")

    # --- Row 1: Checkboxes ---
    frame_checks = ttk.LabelFrame(root, text="Checkboxes", padding=10)
    frame_checks.grid(row=1, column=0, padx=10, pady=5, sticky="ew")

    check_var1 = tk.BooleanVar(value=True)
    check_var2 = tk.BooleanVar(value=False)
    check_var3 = tk.BooleanVar(value=False)

    ttk.Checkbutton(
        frame_checks, text="Enable notifications", variable=check_var1
    ).grid(row=0, column=0, sticky="w")
    ttk.Checkbutton(frame_checks, text="Dark mode", variable=check_var2).grid(
        row=1, column=0, sticky="w"
    )
    ttk.Checkbutton(frame_checks, text="Auto-save", variable=check_var3).grid(
        row=2, column=0, sticky="w"
    )

    # --- Row 1: Radio buttons ---
    frame_radio = ttk.LabelFrame(root, text="Radio Buttons", padding=10)
    frame_radio.grid(row=1, column=1, padx=10, pady=5, sticky="ew")

    radio_var = tk.StringVar(value="medium")
    ttk.Radiobutton(frame_radio, text="Small", variable=radio_var, value="small").grid(
        row=0, column=0, sticky="w"
    )
    ttk.Radiobutton(
        frame_radio, text="Medium", variable=radio_var, value="medium"
    ).grid(row=1, column=0, sticky="w")
    ttk.Radiobutton(frame_radio, text="Large", variable=radio_var, value="large").grid(
        row=2, column=0, sticky="w"
    )

    # --- Row 2: Combobox / Dropdown ---
    frame_combo = ttk.LabelFrame(root, text="Dropdown", padding=10)
    frame_combo.grid(row=2, column=0, padx=10, pady=5, sticky="ew")

    ttk.Label(frame_combo, text="Priority:").grid(row=0, column=0, sticky="w")
    combo = ttk.Combobox(
        frame_combo,
        values=["Low", "Medium", "High", "Critical"],
        state="readonly",
        width=15,
    )
    combo.grid(row=0, column=1, padx=5)
    combo.set("Medium")

    # --- Row 2: Slider ---
    frame_slider = ttk.LabelFrame(root, text="Slider", padding=10)
    frame_slider.grid(row=2, column=1, padx=10, pady=5, sticky="ew")

    slider_var = tk.IntVar(value=50)
    ttk.Label(frame_slider, text="Volume:").grid(row=0, column=0, sticky="w")
    slider = ttk.Scale(
        frame_slider, from_=0, to=100, variable=slider_var, orient="horizontal"
    )
    slider.grid(row=0, column=1, padx=5, sticky="ew")

    # --- Row 3: Table / Treeview ---
    frame_table = ttk.LabelFrame(root, text="Data Table", padding=10)
    frame_table.grid(row=3, column=0, padx=10, pady=5, sticky="nsew", columnspan=2)

    columns = ("name", "status", "priority", "date")
    tree = ttk.Treeview(frame_table, columns=columns, show="headings", height=5)
    tree.heading("name", text="Task Name")
    tree.heading("status", text="Status")
    tree.heading("priority", text="Priority")
    tree.heading("date", text="Due Date")
    tree.column("name", width=180)
    tree.column("status", width=100)
    tree.column("priority", width=100)
    tree.column("date", width=120)

    # Sample data
    data = [
        ("Write unit tests", "In Progress", "High", "2026-03-10"),
        ("Fix login bug", "Open", "Critical", "2026-03-08"),
        ("Update docs", "Done", "Low", "2026-03-15"),
        ("Code review", "In Progress", "Medium", "2026-03-09"),
        ("Deploy v2.0", "Open", "High", "2026-03-12"),
    ]
    for row in data:
        tree.insert("", "end", values=row)

    scrollbar = ttk.Scrollbar(frame_table, orient="vertical", command=tree.yview)
    tree.configure(yscrollcommand=scrollbar.set)
    tree.grid(row=0, column=0, sticky="nsew")
    scrollbar.grid(row=0, column=1, sticky="ns")

    # --- Row 4: Buttons and status ---
    frame_buttons = ttk.Frame(root, padding=10)
    frame_buttons.grid(row=4, column=0, padx=10, pady=5, sticky="ew", columnspan=2)

    status_var = tk.StringVar(value="Ready")
    status_label = ttk.Label(frame_buttons, textvariable=status_var, foreground="green")

    def on_submit():
        status_var.set(f"Submitted: {name_entry.get()}")

    def on_reset():
        name_entry.delete(0, "end")
        name_entry.insert(0, "Test User")
        email_entry.delete(0, "end")
        email_entry.insert(0, "test@example.com")
        combo.set("Medium")
        check_var1.set(True)
        check_var2.set(False)
        check_var3.set(False)
        radio_var.set("medium")
        slider_var.set(50)
        status_var.set("Reset complete")

    ttk.Button(frame_buttons, text="Submit").grid(row=0, column=0, padx=5)
    ttk.Button(frame_buttons, text="Reset", command=on_reset).grid(
        row=0, column=1, padx=5
    )
    ttk.Button(frame_buttons, text="Cancel").grid(row=0, column=2, padx=5)
    status_label.grid(row=0, column=3, padx=20)

    # --- Row 5: Scrollable text area ---
    frame_text = ttk.LabelFrame(root, text="Notes", padding=10)
    frame_text.grid(row=5, column=0, padx=10, pady=5, sticky="nsew", columnspan=2)

    text_area = tk.Text(frame_text, height=4, width=60, wrap="word")
    text_area.insert("1.0", "This is a scrollable text area for testing.\n" * 5)
    text_scroll = ttk.Scrollbar(frame_text, orient="vertical", command=text_area.yview)
    text_area.configure(yscrollcommand=text_scroll.set)
    text_area.grid(row=0, column=0, sticky="nsew")
    text_scroll.grid(row=0, column=1, sticky="ns")

    # Grid weights for resizing
    root.columnconfigure(0, weight=1)
    root.columnconfigure(1, weight=1)
    root.rowconfigure(3, weight=1)
    root.rowconfigure(5, weight=1)

    root.mainloop()


if __name__ == "__main__":
    create_test_app()
