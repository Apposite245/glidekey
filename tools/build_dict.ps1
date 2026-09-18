# Rebuilds cs\assets\words.txt: one entry per line, most frequent first.
# Inputs: the existing word list (dwyl-derived spelling filter) + Norvig's count_1w.txt
# (Google Web Trillion Word corpus unigram counts, https://norvig.com/ngrams/count_1w.txt).
# Entries may contain an apostrophe or a capital I (e.g. "don't", "I'm"); swipe matching
# uses only the letters.
param(
    [string]$Counts = "$PSScriptRoot\count_1w.txt",
    [string]$Spell  = "$PSScriptRoot\spell_words.txt",
    [string]$Out    = "$PSScriptRoot\..\cs\assets\words.txt",
    [int]$Max = 90000
)

$valid = [System.Collections.Generic.HashSet[string]]::new([string[]](Get-Content -LiteralPath $Spell))

# Letters-only form -> contraction. "sole" = the letters form isn't a real word, so replace it;
# otherwise the contraction is added right before the plain word (both become candidates).
$contractions = @{
    dont='don''t'; doesnt='doesn''t'; didnt='didn''t'; isnt='isn''t'; wasnt='wasn''t'; arent='aren''t';
    werent='weren''t'; havent='haven''t'; hasnt='hasn''t'; hadnt='hadn''t'; couldnt='couldn''t';
    wouldnt='wouldn''t'; shouldnt='shouldn''t'; mustnt='mustn''t'; neednt='needn''t'; aint='ain''t';
    im='I''m'; ive='I''ve'; youre='you''re'; youve='you''ve'; youll='you''ll'; youd='you''d';
    theyre='they''re'; theyve='they''ve'; theyll='they''ll'; theyd='they''d'; weve='we''ve';
    thats='that''s'; whats='what''s'; theres='there''s'; heres='here''s'; wheres='where''s';
    whos='who''s'; itll='it''ll'; thatll='that''ll'; hows='how''s'; whens='when''s'; whys='why''s';
    cant='can''t'; wont='won''t'; lets='let''s'; ill='I''ll'; id='I''d'; its='it''s'; hes='he''s';
    shes='she''s'; well='we''ll'; were='we''re'; wed='we''d'; hed='he''d'; shed='she''d'; shell='she''ll'
}
$plainFirst = @('its','well','were','wed','shed','shell','hed','id','ill')   # real words more common than the contraction

$list = [System.Collections.Generic.List[string]]::new()
$seen = [System.Collections.Generic.HashSet[string]]::new()
function Add([string]$w) { if ($seen.Add($w)) { $list.Add($w) } }

foreach ($line in [System.IO.File]::ReadLines((Resolve-Path $Counts))) {
    if ($list.Count -ge $Max) { break }
    $w = $line.Split("`t")[0]
    if ($w -eq 'i') { Add 'I'; continue }
    if ($w -eq 'a') { Add 'a'; continue }
    if ($w -notmatch '^[a-z]{2,24}$') { continue }
    $c = $contractions[$w]
    if ($c) {
        if ($plainFirst -contains $w) { Add $w; Add $c }
        elseif ($valid.Contains($w)) { Add $c; Add $w }
        else { Add $c }
        continue
    }
    if ($valid.Contains($w)) { Add $w }
}

[System.IO.File]::WriteAllLines([System.IO.Path]::GetFullPath($Out), $list)
"wrote $($list.Count) entries to $Out"

