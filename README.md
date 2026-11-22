# Tally

Grocery price comparison for Struga, North Macedonia. Users build a shopping list;
Tally shows which shop, or pair of shops, is cheapest once travel is counted.

## TallyBot

User-Agent: `TallyBot/0.1 (+https://github.com/kukishehu-droid/mktally)`

TallyBot reads the per-store price lists that North Macedonian retailers publish under
the Ministry of Economy and Labour rulebook (in force since 18 April).

- Runs once a night, around 04:00 local time, with pauses between requests
- Checks first whether the price list has changed, and downloads it only if it has
- Respects robots.txt (`User-agent: TallyBot`)
- Reads prices only; never logs in, submits forms or collects personal data

To ask questions or have your site excluded, open an issue here.
