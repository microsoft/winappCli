# Accessibility rules

## Accessibility

### AutomationProperties

- **Every interactive control** must have an `AutomationProperties.Name` or `AutomationProperties.LabeledBy`.
- Add a stable, unique `AutomationProperties.AutomationId` for controls targeted by UI automation tests.
- Use semantic XAML controls — prefer `Button`, `HyperlinkButton`, `ListView` over styled `Border`/`Grid` with click handlers.
- Images must have `AutomationProperties.Name` describing the image purpose (or `AutomationProperties.AccessibilityView="Raw"` for decorative images).

### Keyboard Navigation

- Logical tab order via `TabIndex`.
- `AccessKey` bindings for frequently used actions.
- `KeyboardAccelerator` for shortcut keys.

### Screen Readers

- Support Narrator / NVDA: test that all content is announced correctly.
- Do not rely on colour alone to convey meaning — add icons, text, or patterns.
- Do not use `Visibility.Collapsed` to "hide" content from screen readers (use `AccessibilityView` instead).

### Contrast

- Maintain minimum contrast ratios: 4.5:1 for normal text, 3:1 for large text.
- Test in High Contrast mode.

### Verification Checklist

- [ ] All interactive controls have `AutomationProperties.Name`
- [ ] Keyboard navigation works for the changed area
- [ ] Tested with High Contrast theme enabled
- [ ] Tab through the entire UI with keyboard only
- [ ] Key interactive controls have stable `AutomationProperties.AutomationId` values
- [ ] Switch to Windows High Contrast theme and verify readability
- [ ] Run Narrator and verify all controls are announced correctly
- [ ] Run Accessibility Insights for Windows on the app

---
