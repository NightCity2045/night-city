# Living City tooling

Read-only tools for authored content and offline deterministic scenarios. They do not replace server integration or
load tests, and route reachability still requires a loaded map because CityGraph and Room/Portal Graph are generated
from mapper-authored zones and map entities.

```powershell
python Tools/_NC/LivingCityTooling/living_city_tool.py audit
python Tools/_NC/LivingCityTooling/living_city_tool.py report
python Tools/_NC/LivingCityTooling/living_city_tool.py simulate
python Tools/_NC/LivingCityTooling/living_city_tool.py neural-eval
```

`simulate` uses only active round minutes, a fixed seed and canonical JSON ordering. Its checksum must be identical for
the same fixture. Economy transfers preserve total money supply; actor schedules, abstract travel pressure, relevance
materialization and incident activity are summarized without starting RobustToolbox.
