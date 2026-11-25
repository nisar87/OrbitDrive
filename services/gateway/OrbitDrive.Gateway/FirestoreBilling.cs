namespace OrbitDrive.Gateway;

/// <summary>
/// Billing state in Cloud Firestore: one small document per entry in the "billing" collection (the key's slashes become "~"). Written in batches by
/// <see cref="BillingStore"/>; the gateway is the only reader and writer (server credentials bypass the security rules, which deny everything else).
/// </summary>
public sealed class FirestoreBillingPersistence(Google.Cloud.Firestore.FirestoreDb db) : IBillingPersistence
{
    private const string Collection = "billing";

    public IReadOnlyList<KeyValuePair<string, string>> LoadAll()
    {
        var snapshot = db.Collection(Collection).GetSnapshotAsync().GetAwaiter().GetResult();
        return [.. snapshot.Documents.Where(d => d.ContainsField("json")).Select(d => new KeyValuePair<string, string>(BillingIds.Decode(d.Id), d.GetValue<string>("json")))];
    }

    public void Write(IReadOnlyList<KeyValuePair<string, string?>> changes)
    {
        foreach (var chunk in changes.Chunk(400))
        {
            var batch = db.StartBatch();
            foreach (var (key, json) in chunk)
            {
                var document = db.Collection(Collection).Document(BillingIds.Encode(key));
                if (json is null) batch.Delete(document); else batch.Set(document, new Dictionary<string, object> { ["json"] = json });
            }
            batch.CommitAsync().GetAwaiter().GetResult();
        }
    }
}
