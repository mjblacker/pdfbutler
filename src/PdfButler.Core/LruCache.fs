namespace PdfButler.Core

open System.Collections.Generic

/// Least-recently-used cache bounded by total byte size (not entry count).
/// Not thread-safe: the app uses it only from the UI thread.
type LruCache<'K, 'V when 'K: equality>(budgetBytes: int64, sizeOf: 'V -> int64, onEvict: 'V -> unit) =
    let map = Dictionary<'K, LinkedListNode<KeyValuePair<'K, 'V>>>()
    let order = LinkedList<KeyValuePair<'K, 'V>>() // First = most recently used
    let mutable used = 0L

    let removeNode (node: LinkedListNode<KeyValuePair<'K, 'V>>) =
        order.Remove node
        map.Remove node.Value.Key |> ignore
        used <- used - sizeOf node.Value.Value
        onEvict node.Value.Value

    member _.Budget = budgetBytes
    member _.Used = used
    member _.Count = map.Count
    member _.Keys = map.Keys |> List.ofSeq

    member _.TryGet(key: 'K) : 'V option =
        match map.TryGetValue key with
        | true, node ->
            order.Remove node
            order.AddFirst node
            Some node.Value.Value
        | _ -> None

    /// Look up without changing recency.
    member _.Peek(key: 'K) : 'V option =
        match map.TryGetValue key with
        | true, node -> Some node.Value.Value
        | _ -> None

    member _.Contains(key: 'K) = map.ContainsKey key

    member this.Add(key: 'K, value: 'V) =
        match map.TryGetValue key with
        | true, node -> removeNode node
        | _ -> ()

        let node = order.AddFirst(KeyValuePair(key, value))
        map[key] <- node
        used <- used + sizeOf value

        // Evict least recently used entries, but never the one just added.
        while used > budgetBytes && order.Last <> node do
            removeNode order.Last

    member _.Remove(key: 'K) =
        match map.TryGetValue key with
        | true, node -> removeNode node
        | _ -> ()

    member _.RemoveWhere(pred: 'K -> bool) =
        let doomed = map.Keys |> Seq.filter pred |> List.ofSeq

        for k in doomed do
            removeNode map[k]

    member _.Clear() =
        for kv in List.ofSeq order do
            onEvict kv.Value

        order.Clear()
        map.Clear()
        used <- 0L
