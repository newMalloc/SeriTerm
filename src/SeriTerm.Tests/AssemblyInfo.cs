// 串口是独占的硬件资源：测试并行执行会互相抢同一个端口，
// 因此整个测试程序集禁用并行（硬件类测试的正确做法）。
[assembly: CollectionBehavior(DisableTestParallelization = true)]
