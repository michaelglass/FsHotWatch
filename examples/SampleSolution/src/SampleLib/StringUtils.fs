module SampleLib.StringUtils

let reverse (s: string) = s |> Seq.rev |> System.String.Concat

let isPalindrome (s: string) =
    let normalized = s.ToLowerInvariant()
    normalized = reverse normalized

let wordCount (s: string) =
    if System.String.IsNullOrWhiteSpace(s) then
        0
    else
        s.Split(' ', System.StringSplitOptions.RemoveEmptyEntries).Length
