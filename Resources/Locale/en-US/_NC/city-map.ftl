nc-city-map-title = City map
nc-city-map-loading = Receiving city cartography...
nc-city-map-no-signal = CITY GRID SIGNAL UNAVAILABLE
nc-city-map-grid-only = SEMANTIC CARTOGRAPHY UNAVAILABLE // GRID ONLY

## Admin markup tools
nc-city-map-edit-mode-building = Buildings
nc-city-map-edit-mode-district = Districts
nc-city-map-edit-mode-draw = Draw district
nc-city-map-edit-name-placeholder = Name
nc-city-map-edit-apply = Apply
nc-city-map-edit-redraw = Redraw outline
nc-city-map-edit-undo-point = Undo point
nc-city-map-edit-delete = Delete district
nc-city-map-edit-cancel = Cancel
nc-city-map-edit-hint-building = Click a building, type its name and press Apply. Unnamed buildings are outlined in orange.
nc-city-map-edit-hint-building-selected = Building selected. An empty name clears it.
nc-city-map-edit-hint-district = Click a district to rename, redraw or delete it.
nc-city-map-edit-hint-draw = Click the corners of the district, type its name and press Apply.
nc-city-map-edit-no-building = There is no building here.
nc-city-map-edit-name-too-long = The name must be 1 to {$max} characters long.
nc-city-map-edit-district-shape = A district needs 3 to {$max} corners.
nc-city-map-edit-district-missing = That district no longer exists.
nc-city-map-edit-save-failed = Could not save the city atlas. See the server log.
nc-city-map-edit-building-named = Building named "{$name}". Saved to {$path}.
nc-city-map-edit-building-cleared = Building name cleared. Saved to {$path}.
nc-city-map-edit-district-saved = District "{$name}" saved to {$path}.
nc-city-map-edit-district-deleted = District deleted. Saved to {$path}.

cmd-nccitybuildings-desc = Shows what city map building detection sees on your floor.
cmd-nccitybuildings-help = Usage: nccitybuildings
cmd-nccitybuildings-no-floor = You are not on a floor map.
cmd-nccitybuildings-grid = NavMap grid {$grid}: {$chunks} chunks, at {$x}, {$y}, rotated {$rotation}°.
cmd-nccitybuildings-tiles = Grids used: {$grids}. Floor tiles: {$floor}, walls: {$walls}, doors: {$doors}.
cmd-nccitybuildings-street = Street fill: {$seeds} edge tiles to start from, {$street} street tiles reached.
cmd-nccitybuildings-regions = Enclosed areas: {$regions}, largest {$largest} tiles. Dropped: {$small} too small, {$large} larger than {$limit} tiles.
cmd-nccitybuildings-result = Buildings detected: {$buildings}.
