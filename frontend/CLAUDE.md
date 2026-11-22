# Frontend

React Native, iOS + Android. English interface, v1.

```
src/
  ui/                 design system primitives: Button, Card, Text, Sheet. App-agnostic.
  features/
    list/             components / hooks / screens, colocated
    recommendations/
    stores/
  api/                generated from ../contracts/openapi.json
  navigation/
  theme/
```

## ui/ versus features/

The test: **could this component be lifted into a separate library unmodified?**

If yes it belongs in `ui/`. If it knows what a basket, store or canonical product is, it
belongs in `features/`.

Do not create a single flat components folder. It works until roughly 50 components and
then becomes unnavigable.

## The API client is generated

`src/api/` is generated from `contracts/openapi.json`. Do not hand-write interfaces that
mirror backend DTOs, and do not edit generated files. If a shape is wrong, fix the C# and
regenerate.

## Display rules

These are product requirements, not styling preferences:

- **Every price shows its publication date.** The data is the chains' own published price
  lists, and KAM's lag about two days. Never present prices as live.
- **Promo prices are visually flagged.** They carry explicit expiry dates and a user who
  builds a list today may shop on Thursday.
- **Item coverage is always visible** ("7 of 8 items"). An incomplete basket is shown and
  labelled with what is missing, never silently dropped or hidden.
- **The recommendation explains itself in prose**, with every input figure visible and
  editable. An opaque score gets distrusted.

## Product names

Source data is Macedonian and Albanian. Display the **original name** as primary with the
generated English alias as secondary. Search matches all three.

Do not translate product names in the UI — people recognise the packaging in the shop, not
an English rendering of it.

## Numbers

Currency is Macedonian denar (`den`). Use tabular figures wherever prices align in columns,
and show unit prices (per 100 g / per L) on comparison screens so sizes are comparable.

## List storage

Lists are local to the device in v1. No accounts, no sync, no backend user model.
