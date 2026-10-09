# Svět: chunky a slupky

## Jednotky

| Jednotka | Velikost | Složení |
|---|---|---|
| Blok | 0,5 m | |
| Skupina | 2,5 m | 5³ bloků |
| Chunk | 15 m | 6³ skupin = 30³ bloků |
| Region (soubor) | 75 m | 5³ chunků |

## Slupky kolem kamery (předvolba Ultra)

| Slupka | Dosah | Detail | Generuje se dnes | V paměti dnes | Cíl |
|---|---|---|---|---|---|
| Blízká | 64 m | plný (0,5 m) | všechny chunky koule, i neviditelné | vždy, vlastní rezerva paměti | beze změny |
| Střední | 300 m + 2 chunky | plný | všechny chunky koule, i neviditelné | vždy, nikdy se neodkládá | beze změny (simulace) |
| L0 (dohled) | 1 200 m | plný | všechny chunky v pásu generátoru (svisle jen řádky, kde generátor něco tvoří), i neviditelné | při nedostatku paměti se koule zmenší, neviděné se uvolní první | jen to, co může být vidět |
