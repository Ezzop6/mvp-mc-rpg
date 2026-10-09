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
| Blízká | 64 m | plný (0,5 m) | všechny chunky koule v pásu generátoru | vždy, vlastní rezerva paměti | celá koule, bez pásu |
| Střední | 300 m + 2 chunky | plný | všechny chunky koule v pásu generátoru | vždy, nikdy se neodkládá | celá koule, bez pásu (simulace) |
| L0 (dohled) | 1 200 m | plný | všechny chunky v pásu generátoru (svisle jen řádky, kde generátor něco tvoří), i neviditelné | při nedostatku paměti se koule zmenší, neviděné se uvolní první | jen chunky, na které narazí řídké náhodné paprsky z kamery do všech směrů (na grafice; paprsek si vyžádá první chybějící chunk, o plný se zastaví); chunky s úpravou hráče vždy; co paprsky dlouho nezasáhly, se uvolní; žádné výšky, vrstvy ani pásy |
