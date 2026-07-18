# Offene Klärungen / TODO

## Manuelle Bewertungen vs. Vollpass (offen — nicht Teil von §15 Schritt 9)

**Kontext.** Der Realtime-Listener (§15 Schritt 9) reagiert bewusst **nur auf automatische** Metadaten-
Updates (`MetadataDownload` / `MetadataImport`) und auf neu hinzugefügte Items — **nicht** auf
`MetadataEdit` (manuelle Bearbeitung im UI). Dadurch überschreibt der Listener eine gerade von Hand
gesetzte `CommunityRating` **nicht** sofort. (Siehe `ListenerGate.IsAutomaticMetadataReason`.)

**Lücke.** Der periodische **Scheduled Task** und der **Post-Scan-Vollpass** (`RatingEnrichmentService.
RunAsync` → `EnrichmentRunner`) laufen **nicht** über dieses Gate. Sie wenden die externe Bewertung auf
**alle** Items der aktivierten Bibliotheken an. Eine manuell gesetzte Bewertung wird daher beim nächsten
Vollpass **doch wieder überschrieben** (die Pipeline schreibt, sobald `current != target`, siehe
`RatingPipeline.EvaluateFoundWriteAsync`). Das liegt im Kernmodell „das Plugin besitzt CommunityRating".

**Zu entscheiden.** Sollen **gesperrte Items** (Jellyfin `BaseItem.IsLocked`) generell übersprungen
werden — sowohl im Listener als auch im Vollpass —, damit der Nutzer eine Bewertung dauerhaft schützen
kann, indem er das Item sperrt? Das wäre der Jellyfin-native Weg.

Mögliche Umsetzung (späterer Schritt, z. B. Schritt 10):
- Vollpass: in `RatingEnrichmentService.BuildLibraryQuery` bzw. beim Einsammeln in `BuildWorkItems`
  Items mit `IsLocked == true` auslassen (oder ein `InternalItemsQuery`-Filter, falls vorhanden).
- Listener: in `EnrichItemAsync` (oder im Gate) `IsLocked` prüfen und überspringen.
- Alternativ ein eigenes Config-Flag „gesperrte Bewertungen respektieren".

Bis zur Entscheidung gilt: manuelle Werte bleiben nur bis zum nächsten Vollpass erhalten.
