# NexZen Desktop UI Design System

This rule defines the standardized UI design language for the NexZen Desktop application, derived from the `SuperMenuControl`. Whenever you create or modify UI components, you MUST adhere to these design principles.

## 1. Containers and Menus
- **Backgrounds**: Use clean white (`#FFFFFF`) or very light gray (`#F9F9F9`).
- **Borders**: Use subtle borders (`BorderThickness="1"`) with light gray color (`BorderBrush="#CCCCCC"` or `#D0D0D0`).
- **Corner Radius**: Use `CornerRadius="8"` for all main popup containers, context menus, and panels.
- **Shadows**: Use soft drop shadows to create depth. For example, `BoxShadow="0 4 10 0 #40000000"` for popups/menus or `BoxShadow="0 2 5 0 #20000000"` for flatter elements.

## 2. Icon Buttons
- **Size**: Typically `36x36` or `30x30`.
- **Background**: `#F5F5F5` (normal state).
- **Hover State**: `#E0E0E0`.
- **Border**: 1px `#D0D0D0`.
- **Corner Radius**: `6` for standard buttons, or fully rounded (`Width/2`) for circular buttons (e.g. `15` for a `30x30` button).
- **Icons**: Use SVG Paths (`PathIcon` or `Path`) with dark gray foreground (`#333333` or `#333`). Do NOT use transparent backgrounds for floating buttons over images; always give them a solid background.

## 3. List Items (Menu entries)
- **Height**: `38`.
- **Background**: `#FFFFFF` (normal), `#F0F0F0` (hover).
- **Foreground**: `#333333` for text.
- **Border**: 1px `#D0D0D0`.
- **Corner Radius**: `6`.
- **Padding**: `12,0`.
- **Alignment**: Text should be Left-aligned.

## 4. Highlighted Items
- For primary or special actions, use a soft green accent.
- **Background**: `#E8F5E9` (normal), `#C8E6C9` (hover).
- **Border**: 1px `#81C784`.
- **Foreground**: `#333333`.

## General Guidelines
- **No pure black**: Use `#333333` or `#666666` for text and icons instead of pure black.
- **Consistent spacing**: Use margins of `4`, `8`, or `12` for logical grouping.
- **Micro-interactions**: Always provide a `:pointerover` visual state for clickable elements.
