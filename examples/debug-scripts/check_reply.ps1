$record = [Console]::In.ReadToEnd()
if ($record.Contains('pong')) { [Console]::Out.WriteLine('PASS: reply contains pong') }
else { [Console]::Error.WriteLine('FAIL: expected pong in reply'); exit 1 }
