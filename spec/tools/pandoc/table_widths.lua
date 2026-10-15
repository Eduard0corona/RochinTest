-- Anchos de columna proporcionales al contenido, con un mínimo para que la palabra más
-- larga de cada columna no se parta. Evita el reparto en partes iguales de pandoc.
local CHARS_PER_ROW = 100   -- caracteres aproximados por ancho de página a 8.5 pt, con márgenes de celda

local function ulen(s) return utf8.len(s) or #s end

local function measure(blocks)
  local s = pandoc.utils.stringify(blocks)
  local longest = 0
  for word in s:gmatch("%S+") do
    local l = ulen(word)
    if l > longest then longest = l end
  end
  return ulen(s), longest
end

function Table(tbl)
  local n = #tbl.colspecs
  if n < 2 then return nil end
  local maxlen, maxword = {}, {}
  for i = 1, n do maxlen[i] = 3; maxword[i] = 3 end
  local function scan(rows)
    for _, row in ipairs(rows) do
      for i, cell in ipairs(row.cells) do
        if i <= n then
          local l, w = measure(cell.contents)
          if l > maxlen[i] then maxlen[i] = l end
          if w > maxword[i] then maxword[i] = w end
        end
      end
    end
  end
  scan(tbl.head.rows)
  for _, body in ipairs(tbl.bodies) do scan(body.body) end
  local minf, summin, weights, sumw = {}, 0, {}, 0
  for i = 1, n do
    minf[i] = math.min((maxword[i] + 4) / CHARS_PER_ROW, 0.30)
    summin = summin + minf[i]
    weights[i] = math.min(maxlen[i], 140) ^ 0.7
    sumw = sumw + weights[i]
  end
  if summin > 0.95 then
    for i = 1, n do minf[i] = minf[i] * 0.95 / summin end
    summin = 0.95
  end
  for i = 1, n do
    local f = minf[i] + (1 - summin) * weights[i] / sumw
    tbl.colspecs[i] = {tbl.colspecs[i][1], f}
  end
  return tbl
end
