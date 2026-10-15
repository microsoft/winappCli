---
name: inventory-manage-app
description: "Build a WinUI 3 warehouse inventory management LOB app with a dynamic table, CRUD operations, and PDF export"
type: new
app_name: InventoryManager
requirements:
  - "A table shows inventory items with at least the default columns Name, SKU, Category, Quantity, Unit Price, and Location"
  - "The user can add a new item and edit cell values directly in the table"
  - "The user can delete selected items, with a confirmation before data is removed"
  - "The user can add a custom column and choose its type (text, number, date, or yes/no)"
  - "The user can rename and remove columns they added; the default columns cannot be removed"
  - "Clicking a column header sorts the table ascending/descending"
  - "A search box filters the visible rows by matching across all columns as the user types"
  - "Export to PDF exports the current view (respecting the active search/filter) and lets the user choose where to save the file"
  - "The exported PDF contains a formatted table with the visible columns and rows"
  - "Inventory data persists across app restarts"
---

Build me a warehouse inventory management app. Our warehouse team currently tracks everything in spreadsheets and it's a mess — I need a proper desktop app for it.

The main view should be a big table showing our stock: name, SKU, category, quantity, unit price, and location. Users need to be able to add new items, delete old ones, edit cells right in the table, and sort by clicking column headers. Also need a search box to quickly find stuff.

The key thing is that every warehouse is different, so users need to be able to customize the table — add their own columns (like "Expiry Date" or "Fragile yes/no"), rename columns, and remove ones they added. The default columns should stay though.

We also need a PDF export so managers can print the current view for stocktake meetings. And obviously the data needs to persist between sessions — nobody wants to re-enter hundreds of items every time they open the app.
