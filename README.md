# Boutquin.Storage

Boutquin.Storage is a collection of data storage algorithms implemented in C# using a clean architecture approach. This repository is designed to provide efficient and scalable solutions for data-intensive applications.

## Features

- B+ Trees
- Consistent Hashing
- Gossip Protocol
- Two-Phase Commit (2PC)
- Optimistic Concurrency Control

## Getting Started

### Installation

You can install the packages from NuGet:

```sh
dotnet add package Boutquin.Storage.Domain
dotnet add package Boutquin.Storage.Application
dotnet add package Boutquin.Storage.Infrastructure
dotnet add package Boutquin.Storage.Api
```

## Contributing

Contributions are welcome! Please read the [contributing guidelines](CONTRIBUTING.md) first.

### Reporting Bugs

If you find a bug in the project, please report it by opening an issue on the [Issues](https://github.com/Boutquin/Boutquin.Storage/issues) page. Make sure to include the following information:

- A clear and descriptive title.
- Steps to reproduce the issue.
- Expected and actual behavior.
- Screenshots or code snippets, if applicable.
- Any other relevant information, such as the operating system and version.

### Suggesting Enhancements

If you have an idea for an enhancement or new feature, we would love to hear about it! Please submit a suggestion by opening an issue on the [Issues](https://github.com/Boutquin/Boutquin.Storage/issues) page. Provide the following details:

- A clear and descriptive title.
- A detailed description of the proposed enhancement.
- Any relevant use cases or benefits.
- Any potential downsides or trade-offs.

### Contributing Code

To contribute code to this project, follow these steps:

1. **Fork the repository**: Click the "Fork" button on the top right of the repository page and clone your fork locally.
    ```bash
    git clone https://github.com/your-username/Boutquin.Storage.git
    cd Boutquin.Storage
    ```

2. **Create a branch**: Create a new branch for your feature or bugfix.
    ```bash
    git checkout -b feature-or-bugfix-name
    ```

3. **Make your changes**: Implement your feature or bugfix, following the style guides outlined in the [contributing guidelines](CONTRIBUTING.md).

4. **Commit your changes**: Write clear and concise commit messages.
    ```bash
    git commit -m "Description of the changes made"
    ```

5. **Push to your fork**: Push your changes to your forked repository.
    ```bash
    git push origin feature-or-bugfix-name
    ```

6. **Open a pull request**: Navigate to the original repository and open a pull request. Provide a clear and descriptive title and description of your changes.

## License

This project is licensed under the Apache 2.0 License - see the [LICENSE](LICENSE) file for details.

## Contact Information

For any inquiries, please open an issue on this repository or reach out via [GitHub Discussions](https://github.com/Boutquin/Boutquin.Storage/discussions).

## Acknowledgments

- [Martin Kleppmann](https://martin.kleppmann.com/) for writing [Algorithms in Designing Data-Intensive Applications](algorithms-in-designing-data-intensive-applications.md)
.
- ChatGPT for aiding in writing and documenting the code.